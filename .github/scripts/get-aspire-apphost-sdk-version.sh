#!/usr/bin/env bash
set -euo pipefail

apphost_path="${1:-src/GroupSplit.AppHost/GroupSplit.AppHost.csproj}"
if [[ ! -f "$apphost_path" ]]; then
  echo "AppHost was not found: $apphost_path" >&2
  exit 1
fi

version="$(sed -nE 's/^[[:space:]]*<PackageReference Include="Aspire\.Hosting\.AppHost" Version="([^"]+)".*/\1/p' "$apphost_path" | head -n 1)"
if [[ -z "$version" ]]; then
  echo "Could not find Aspire.Hosting.AppHost version in $apphost_path" >&2
  exit 1
fi
printf '%s\n' "$version"
