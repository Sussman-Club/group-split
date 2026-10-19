#!/usr/bin/env bash
set -euo pipefail

# Check the credentials needed to update the existing Komodo stack.

missing=0
for name in KOMODO_URL KOMODO_API_KEY KOMODO_API_SECRET KOMODO_REGISTRY_ACCOUNT; do
  if [ -z "${!name:-}" ]; then
    echo "${name} is not set on the production environment" >&2
    missing=1
  fi
done

[ "$missing" -eq 0 ]
