#!/usr/bin/env bash
set -euo pipefail

project_path="${1:-src/GroupSplit.AppHost/GroupSplit.AppHost.csproj}"

if [[ ! -f "$project_path" ]]; then
  echo "AppHost project was not found: $project_path" >&2
  exit 1
fi

# The AppHost SDK is a root-project attribute, so MSBuild does not surface it as
# an evaluated property. Read the declaration that selects the SDK instead.
version="$(sed -nE 's#^[[:space:]]*<Project[[:space:]]+Sdk="Aspire\.AppHost\.Sdk/([^"]+)".*#\1#p' "$project_path" | head -n 1)"

if [[ -z "$version" ]]; then
  echo "Could not find Aspire.AppHost.Sdk/<version> in $project_path" >&2
  exit 1
fi

printf '%s\n' "$version"
