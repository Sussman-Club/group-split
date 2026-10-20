#!/usr/bin/env bash
set -euo pipefail

# Build and push the release, then replace image tags in the generated environment with
# registry digests.

output_dir="${COMPOSE_OUTPUT_DIR:-src/GroupSplit.AppHost/aspire-output}"
env_file="$output_dir/.env.production"

# Only these GitHub-facing values become Aspire parameters. A new repository secret stays
# out until it is added here.
parameters="
  WEB_HOSTNAME DASHBOARD_TOKEN DB_SERVER_PASSWORD KEYCLOAK_PASSWORD CACHE_PASSWORD
  KEYCLOAK_ADMIN_USER KEYCLOAK_ADMIN_PASSWORD KEYCLOAK_ACCOUNT_DELETION_SECRET
  STORAGE_ACCESSKEY STORAGE_SECRETKEY
  SMTP_ENABLED SMTP_HOST SMTP_PORT SMTP_FROM SMTP_USER SMTP_PASSWORD
  GOOGLE_SIGN_IN_ENABLED GOOGLE_CLIENT_ID GOOGLE_CLIENT_SECRET
  PLAID_ENABLED PLAID_CLIENT_ID PLAID_SECRET PLAID_ENV PLAID_REDIRECT_URI
  BANK_KEY_CERTIFICATE
  RECEIPT_TRANSCRIPTION_PROVIDER
  VERYFI_ENABLED VERYFI_CLIENT_ID VERYFI_USERNAME VERYFI_API_KEY
  VERYFI_LOG_RAW_RESPONSES
  AZURE_OPENAI_ENABLED AZURE_OPENAI_ENDPOINT AZURE_OPENAI_MODEL AZURE_OPENAI_API_KEY
  AZURE_OPENAI_DECISION_DEPLOYMENT
"

for name in $parameters; do
  value="${!name:-}"
  if [ -n "$value" ]; then
    export "Parameters__$(printf %s "$name" | tr '[:upper:]' '[:lower:]')=$value"
  fi
  unset "$name"
done

# Run push and Compose preparation together. Separate runs can stamp different release tags.
aspire do release-compose --environment production --non-interactive

# Pin each image to the digest CI pushed before the files leave the runner.
image_variables=(API_IMAGE WEB_IMAGE MIGRATIONS_INTERNAL_IMAGE REALM_SETTINGS_IMAGE)
declare -A tagged_images
declare -A pinned_images

env_value() {
  local key="$1"
  local value
  value="$(awk -F= -v key="$key" '$1 == key {sub(/^[^=]*=/, ""); print; exit}' "$env_file")"
  [ -n "$value" ] || {
    echo "Generated environment is missing $key." >&2
    exit 1
  }
  printf '%s' "$value"
}

replace_env_value() {
  local key="$1"
  local value="$2"
  local temporary
  temporary="$(mktemp)"
  awk -v key="$key" -v value="$value" '
    index($0, key "=") == 1 { print key "=" value; found=1; next }
    { print }
    END { if (!found) exit 1 }
  ' "$env_file" > "$temporary"
  mv "$temporary" "$env_file"
}

for image_variable in "${image_variables[@]}"; do
  tagged_images["$image_variable"]="$(env_value "$image_variable")"
  repository="${tagged_images[$image_variable]%:*}"
  pinned_images["$image_variable"]="$(docker image inspect \
    --format '{{range .RepoDigests}}{{println .}}{{end}}' \
    "${tagged_images[$image_variable]}" \
    | awk -v prefix="${repository}@sha256:" 'index($0, prefix) == 1 {print; exit}' || true)"
  [ -n "${pinned_images[$image_variable]}" ] || {
    echo "Could not resolve a registry digest for ${tagged_images[$image_variable]}." >&2
    exit 1
  }
  replace_env_value "$image_variable" "${pinned_images[$image_variable]}"
done

# Keep the manifest secret-free. .env.production stays on the runner.
{
  echo "deployment_version=$DEPLOYMENT_VERSION"
  echo "commit=$DEPLOYMENT_COMMIT"
  echo "environment=production"
  echo "created_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  for image_variable in "${image_variables[@]}"; do
    image_name="${image_variable%_IMAGE}"
    image_name="${image_name,,}"
    echo "image.${image_name}.tag=${tagged_images[$image_variable]}"
    echo "image.${image_name}.digest=${pinned_images[$image_variable]#*@}"
    echo "image.${image_name}.reference=${pinned_images[$image_variable]}"
  done
} > "$output_dir/deployment-manifest.txt"
