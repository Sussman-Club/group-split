#!/usr/bin/env bash
set -euo pipefail

# Synthetic values only: no local user secrets or production environment is loaded.
script="$(dirname "$0")/validate-deployment-inputs.sh"
base=(WEB_HOSTNAME=https://example.test DASHBOARD_TOKEN=test DB_SERVER_PASSWORD=test
      KEYCLOAK_PASSWORD=test KEYCLOAK_ADMIN_USER=test KEYCLOAK_ADMIN_PASSWORD=test
      KEYCLOAK_ACCOUNT_DELETION_SECRET=test CACHE_PASSWORD=test STORAGE_ACCESSKEY=test STORAGE_SECRETKEY=test
      MCP_ISSUER=https://example.test/idp/realms/group-split MCP_RESOURCE=https://example.test/mcp
      MCP_EXCHANGE_CLIENT_ID=test MCP_EXCHANGE_CLIENT_SECRET=test)

env -i PATH="$PATH" "${base[@]}" bash "$script" >/dev/null
if env -i PATH="$PATH" "${base[@]}" MCP_EXCHANGE_CLIENT_SECRET= bash "$script" >/dev/null 2>&1; then
  echo "Expected missing MCP secret to fail" >&2; exit 1
fi
if env -i PATH="$PATH" "${base[@]}" MCP_RESOURCE=https://other.test/mcp bash "$script" >/dev/null 2>&1; then
  echo "Expected mismatched MCP resource to fail" >&2; exit 1
fi
if env -i PATH="$PATH" "${base[@]}" MCP_ISSUER=https://other.test/realms/group-split bash "$script" >/dev/null 2>&1; then
  echo "Expected mismatched issuer to fail" >&2; exit 1
fi
if env -i PATH="$PATH" "${base[@]}" WEB_HOSTNAME=http://example.test MCP_RESOURCE=http://example.test/mcp MCP_ISSUER=http://example.test/idp/realms/group-split bash "$script" >/dev/null 2>&1; then
  echo "Expected non-HTTPS resource to fail" >&2; exit 1
fi
echo "MCP deployment input validation passed."
