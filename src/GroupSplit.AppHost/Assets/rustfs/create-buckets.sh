#!/bin/sh
# Creates the buckets the API expects, then hands the container over to the RustFS server.
#
# `AddBucket()` in the AppHost is honoured only by the run-mode orchestrator, which creates each
# bucket over S3 once the server reports ready. A deployed stack has no orchestrator, so without
# this the server comes up on an empty volume, every receipt upload is refused with NoSuchBucket,
# and the API reports that as an internal error.
#
# RustFS has no equivalent of the Postgres image's /docker-entrypoint-initdb.d, and neither a flag
# nor a variable that declares a bucket, so the S3 API is the only way in. curl signs SigV4 itself,
# which keeps this to what the image already ships: there is no aws or mc binary here, and no
# openssl to sign with by hand.

set -eu

: "${RUSTFS_ACCESS_KEY:?the storage credentials are not in the environment}"
: "${RUSTFS_SECRET_KEY:?the storage credentials are not in the environment}"

address="${RUSTFS_ADDRESS:-:9000}"
port="${address##*:}"
port="${port:-9000}"
endpoint="http://127.0.0.1:${port}"
region="${RUSTFS_REGION:-us-east-1}"
ready_file=/tmp/buckets-ready

# The healthcheck waits on this file, so a restart must not inherit the last run's answer.
rm -f "$ready_file"

# The image's own entrypoint normalises the arguments and the data volumes, so it still runs --
# in the background, because a bucket can only be created once the server is listening.
/entrypoint.sh "$@" &
server=$!

trap 'kill -TERM "$server" 2>/dev/null || true' INT TERM

fail() {
    echo "create-buckets: $1" >&2
    kill -TERM "$server" 2>/dev/null || true
    wait "$server" 2>/dev/null || true
    exit 1
}

# Any answer at all proves it is serving: an unauthenticated request is refused with 403, which
# curl reports as success as long as it is not asked to treat HTTP errors as failures.
waited=0
until curl -s -o /dev/null "${endpoint}/" 2>/dev/null; do
    kill -0 "$server" 2>/dev/null || fail "the server exited before it began listening"
    [ "$waited" -lt 60 ] || fail "the server did not answer on port ${port} within 60s"
    waited=$((waited + 1))
    sleep 1
done

# Passed on stdin rather than in the argument list, which every process in the container can read.
escape() { printf '%s' "$1" | sed 's/[\\"]/\&/g'; }
credentials=$(printf 'user = "%s:%s"\n' \
    "$(escape "$RUSTFS_ACCESS_KEY")" "$(escape "$RUSTFS_SECRET_KEY")")

for bucket in ${GROUPSPLIT_INIT_BUCKETS:-}; do
    attempt=1
    while :; do
        status=$(printf '%s\n' "$credentials" | curl --config - \
            --silent --output /dev/null --write-out '%{http_code}' \
            --request PUT --aws-sigv4 "aws:amz:${region}:s3" \
            "${endpoint}/${bucket}" || echo 000)

        # 409 is BucketAlreadyOwnedByYou: the data volume already carries it from an earlier run.
        case "$status" in
            200 | 204 | 409)
                echo "create-buckets: bucket '${bucket}' is ready (HTTP ${status})"
                break
                ;;
        esac

        [ "$attempt" -lt 10 ] || fail "creating bucket '${bucket}' failed with HTTP ${status}"
        attempt=$((attempt + 1))
        sleep 2
    done
done

touch "$ready_file"

# From here the container is the server, and the server's exit status is the container's.
wait "$server"
