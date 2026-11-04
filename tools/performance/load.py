"""Bounded, loopback-only mixed API load against disposable Aspire data. Standard library only."""
import argparse
import concurrent.futures
import datetime
import json
import os
import pathlib
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


class NoRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def run_stage(base, identities, count, duration, receipt):
    records = []
    lock = threading.Lock()
    start = time.monotonic()
    finish = start + duration

    def user(identity):
        client = urllib.request.build_opener(NoRedirects())

        def send(operation, method, path, data=None, content_type=None):
            headers = {"Authorization": "Bearer " + identity["token"]}
            if content_type:
                headers["Content-Type"] = content_type
            request = urllib.request.Request(base + path, data=data, headers=headers, method=method)
            before = time.perf_counter()
            result = None
            try:
                with client.open(request, timeout=30) as response:
                    status = response.status
                    body = response.read()
                    if operation == "create":
                        result = json.loads(body)
            except urllib.error.HTTPError as error:
                status = error.code
                error.close()
            except (urllib.error.URLError, TimeoutError, OSError):
                status = 0
            with lock:
                records.append((operation, (time.perf_counter() - before) * 1000, status))
            return result

        cycle = 0
        while time.monotonic() < finish:
            group = identity.get("groupId")
            query = urllib.parse.urlencode({"page": cycle % 5 + 1, "pageSize": 50,
                "sortBy": "dateTime", "sortDescending": "true", **({"groupId": group} if group else {})})
            send("browse", "GET", "/transactions?" + query)
            if group:
                send("group", "GET", f"/groups/{group}/activity?page={cycle % 5 + 1}&pageSize=50")
            if cycle % 5 == 0:
                payload = {"name": "Load test " + uuid.uuid4().hex[:12], "amount": 1.25,
                    "dateTime": datetime.datetime.now(datetime.timezone.utc).isoformat(), "currency": "USD"}
                if group:
                    payload["groupId"] = group
                created = send("create", "POST", "/transactions", json.dumps(payload).encode(), "application/json")
                if receipt and cycle % 10 == 0 and created and created.get("id"):
                    boundary = "gs" + uuid.uuid4().hex
                    body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="load.png"\r\n'
                        'Content-Type: image/png\r\n\r\n').encode() + receipt + f"\r\n--{boundary}--\r\n".encode()
                    send("upload", "POST", f'/transactions/{created["id"]}/receipt-attachments', body,
                         "multipart/form-data; boundary=" + boundary)
            cycle += 1
            time.sleep(0.2)

    with concurrent.futures.ThreadPoolExecutor(max_workers=count) as pool:
        list(pool.map(user, identities[:count]))
    elapsed = time.monotonic() - start
    summary = {"users": count, "seconds": round(elapsed, 2), "requests": len(records),
               "requestsPerSecond": round(len(records) / elapsed, 2), "operations": {}}
    for operation in sorted({r[0] for r in records}):
        rows = [r for r in records if r[0] == operation]
        times = sorted(r[1] for r in rows)
        statuses = {str(s): sum(r[2] == s for r in rows) for s in sorted({r[2] for r in rows})}
        summary["operations"][operation] = {"count": len(rows), "statusCounts": statuses,
            "errors": sum(not 200 <= r[2] < 300 for r in rows),
            **{f"p{p}Ms": round(times[min(len(times) - 1, int((len(times) - 1) * p / 100))], 2) for p in (50, 95, 99)}}
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True, help="Local API root, or local web root ending in /api")
    parser.add_argument("--seconds", type=int, default=60, help="Duration of each of the 10, 25, 50 user stages")
    parser.add_argument("--receipt", type=pathlib.Path, help="Synthetic PNG receipt; uploads only, no paid transcription")
    args = parser.parse_args()
    url = urllib.parse.urlsplit(args.base_url)
    if url.hostname not in ("localhost", "127.0.0.1", "::1") or url.scheme not in ("http", "https") or url.username or url.password or url.query or url.fragment:
        parser.error("Use a loopback URL for an isolated, disposable local stack.")
    if not 1 <= args.seconds <= 600:
        parser.error("--seconds must be between 1 and 600")
    identities = json.loads(os.environ.get("GROUPSPLIT_LOAD_IDENTITIES", "[]"))
    if len(identities) < 50 or len({i.get("token") for i in identities}) < 50 or any(not i.get("token") for i in identities):
        parser.error("GROUPSPLIT_LOAD_IDENTITIES must contain at least 50 distinct test-user bearer tokens.")
    receipt = args.receipt.read_bytes() if args.receipt else None
    if receipt is not None and (not receipt.startswith(b"\x89PNG\r\n\x1a\n") or len(receipt) > 10 * 1024 * 1024):
        parser.error("Use a synthetic PNG no larger than 10 MB")
    for count in (10, 25, 50):
        print(json.dumps(run_stage(args.base_url.rstrip("/"), identities, count, args.seconds, receipt)), flush=True)


if __name__ == "__main__":
    main()
