"""Exercise the MCP token-exchange configuration against an isolated Keycloak.

Requires Docker and the local quay.io/keycloak/keycloak:26.6 image. All users,
credentials and data below are synthetic. No existing container is modified.
"""
import base64
import json
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

IMAGE = "quay.io/keycloak/keycloak:26.6"
RESOURCE = "https://mcp.example.test/mcp"
SECRET = "synthetic-test-secret"
NAME = "groupsplit-mcp-test-" + uuid.uuid4().hex[:12]


def audience(name, client=None, custom=None):
    config = {"access.token.claim": "true", "id.token.claim": "false"}
    config["included.client.audience" if client else "included.custom.audience"] = client or custom
    return {"name": name, "protocol": "openid-connect", "protocolMapper": "oidc-audience-mapper", "config": config}


def claims(token):
    data = token.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(data + "=" * (-len(data) % 4)))


def audiences(token_claims):
    value = token_claims["aud"]
    return [value] if isinstance(value, str) else value


def request(url, form):
    payload = urllib.parse.urlencode(form).encode()
    try:
        with urllib.request.urlopen(urllib.request.Request(url, data=payload), timeout=15) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)


def main():
    realm = {
        "realm": "mcp-test", "enabled": True, "sslRequired": "none",
        "roles": {"realm": [{"name": "groupsplit-staff"}]},
        "clientScopes": [
            {"name": "mcp:read", "protocol": "openid-connect", "attributes": {"include.in.token.scope": "true"}},
            {"name": "basic", "protocol": "openid-connect", "protocolMappers": [
                {"name": "subject", "protocol": "openid-connect", "protocolMapper": "oidc-sub-mapper",
                 "config": {"access.token.claim": "true", "id.token.claim": "true"}}]},
            {"name": "roles", "protocol": "openid-connect", "protocolMappers": [
                {"name": "realm roles", "protocol": "openid-connect", "protocolMapper": "oidc-usermodel-realm-role-mapper",
                 "config": {"access.token.claim": "true", "claim.name": "realm_access.roles", "jsonType.label": "String", "multivalued": "true"}}]},
        ],
        "clients": [
            {"clientId": "api", "enabled": True, "publicClient": True, "standardFlowEnabled": False},
            {"clientId": "mcp-exchange", "enabled": True, "publicClient": False, "secret": SECRET,
             "standardFlowEnabled": False, "attributes": {"standard.token.exchange.enabled": "true"},
             "defaultClientScopes": ["basic", "profile", "roles"],
             "protocolMappers": [audience("api-audience", client="api")]},
            {"clientId": "assistant", "enabled": True, "publicClient": True, "directAccessGrantsEnabled": True,
             "standardFlowEnabled": False, "defaultClientScopes": ["basic", "profile", "roles"], "optionalClientScopes": ["mcp:read"],
             "protocolMappers": [audience("mcp-resource", custom=RESOURCE), audience("exchange-audience", client="mcp-exchange")]},
            {"clientId": "wrong-audience", "enabled": True, "publicClient": True, "directAccessGrantsEnabled": True,
             "standardFlowEnabled": False, "defaultClientScopes": ["basic", "profile", "roles"], "optionalClientScopes": ["mcp:read"],
             "protocolMappers": [audience("mcp-resource", custom=RESOURCE)]},
        ],
        "users": [
            {"username": "member", "email": "member@example.test", "emailVerified": True, "firstName": "Test", "lastName": "Member", "enabled": True, "credentials": [{"type": "password", "value": "test-password", "temporary": False}]},
            {"username": "staff", "email": "staff@example.test", "emailVerified": True, "firstName": "Test", "lastName": "Staff", "enabled": True, "realmRoles": ["groupsplit-staff"],
             "credentials": [{"type": "password", "value": "test-password", "temporary": False}]},
        ],
    }
    with tempfile.TemporaryDirectory(prefix="mcp-keycloak-") as temp:
        realm_file = Path(temp, "mcp-test-realm.json")
        realm_file.write_text(json.dumps(realm), encoding="utf-8")
        started = False
        try:
            subprocess.run(["docker", "run", "-d", "--name", NAME, "-p", "127.0.0.1::8080",
                            "-v", f"{realm_file}:/opt/keycloak/data/import/mcp-test-realm.json:ro", IMAGE, "start-dev", "--import-realm"],
                           check=True, stdout=subprocess.DEVNULL)
            started = True
            port = subprocess.check_output(["docker", "port", NAME, "8080/tcp"], text=True).strip().rsplit(":", 1)[1]
            base = f"http://127.0.0.1:{port}/realms/mcp-test/protocol/openid-connect"
            for _ in range(120):
                try:
                    with urllib.request.urlopen(base + "/certs", timeout=2):
                        break
                except (OSError, urllib.error.URLError):
                    time.sleep(1)
            else:
                logs = subprocess.run(["docker", "logs", NAME], capture_output=True, text=True, check=False)
                raise RuntimeError(f"Isolated Keycloak did not become ready. Container logs:\n{logs.stdout}\n{logs.stderr}")
            for username in ("member", "staff"):
                status, initial = request(base + "/token", {"grant_type": "password", "client_id": "assistant",
                    "username": username, "password": "test-password", "scope": "openid mcp:read"})
                assert status == 200, f"test sign-in failed: {initial}"
                token = initial["access_token"]
                original = claims(token)
                assert "sub" in original, f"initial token has no subject; claim names: {sorted(original)}"
                assert RESOURCE in audiences(original) and "mcp-exchange" in audiences(original)
                assert "mcp:read" in original["scope"].split()
                form = {"grant_type": "urn:ietf:params:oauth:grant-type:token-exchange",
                        "subject_token_type": "urn:ietf:params:oauth:token-type:access_token",
                        "subject_token": token, "audience": "api", "client_id": "mcp-exchange", "client_secret": SECRET}
                status, exchanged = request(base + "/token", form)
                assert status == 200, f"token exchange failed: {exchanged}"
                result = claims(exchanged["access_token"])
                assert result["sub"] == original["sub"]
                assert "api" in audiences(result)
                roles = result.get("realm_access", {}).get("roles", [])
                assert ("groupsplit-staff" in roles) == (username == "staff")
                form["client_secret"] = "wrong"
                assert request(base + "/token", form)[0] in (400, 401)
            status, bad = request(base + "/token", {"grant_type": "password", "client_id": "wrong-audience",
                "username": "member", "password": "test-password", "scope": "openid mcp:read"})
            assert status == 200, f"test sign-in failed: {bad}"
            status, _ = request(base + "/token", {"grant_type": "urn:ietf:params:oauth:grant-type:token-exchange",
                "subject_token_type": "urn:ietf:params:oauth:token-type:access_token", "subject_token": bad["access_token"],
                "audience": "api", "client_id": "mcp-exchange", "client_secret": SECRET})
            assert status in (400, 403), "Keycloak accepted a token without the exchange-client audience"
            print("Keycloak token exchange passed: audience, identity, staff roles, invalid secret and wrong audience.")
        finally:
            if started:
                subprocess.run(["docker", "rm", "--force", NAME], stdout=subprocess.DEVNULL, check=False)


if __name__ == "__main__":
    main()
