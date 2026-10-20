#!/usr/bin/env bash
set -euo pipefail

# Reuse an app-v tag on HEAD for a rerun; otherwise claim the requested next version.

commit="$(git rev-parse HEAD)"
existing="$(git tag --points-at HEAD --list 'app-v*' | head -1)"

if [ -n "$existing" ]; then
  echo "HEAD is already tagged $existing; deploying that version again."
  version="$existing"
else
  latest="$(git tag --list 'app-v*' --sort=-v:refname | head -1)"

  if [ -z "$latest" ]; then
    version="app-v0.1.0"
  else
    IFS=. read -r major minor patch <<<"${latest#app-v}"

    case "$BUMP" in
      major) version="app-v$((major + 1)).0.0" ;;
      minor) version="app-v${major}.$((minor + 1)).0" ;;
      patch) version="app-v${major}.${minor}.$((patch + 1))" ;;
      *) echo "::error::Unknown bump '$BUMP'." >&2; exit 1 ;;
    esac
  fi

  echo "Last release: ${latest:-none}. Claiming $version ($BUMP) for $commit."
  gh api "repos/$GITHUB_REPOSITORY/git/refs" \
    -f "ref=refs/tags/$version" -f "sha=$commit" > /dev/null
fi

echo "version=$version" >> "$GITHUB_OUTPUT"
echo "commit=$commit" >> "$GITHUB_OUTPUT"
echo "DEPLOYMENT_VERSION=$version" >> "$GITHUB_ENV"
echo "DEPLOYMENT_COMMIT=$commit" >> "$GITHUB_ENV"
echo "Deployment__Version=$version" >> "$GITHUB_ENV"
