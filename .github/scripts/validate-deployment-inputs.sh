#!/usr/bin/env bash
set -euo pipefail

# Validates the production inputs mapped by deploy.yml before the image build starts.

missing=0

value() {
  printf '%s' "${!1:-}"
}

require() {
  for name in "$@"; do
    if [ -z "$(value "$name")" ]; then
      echo "${name} is not set on the production environment" >&2
      missing=1
    fi
  done
}

require_when() {
  local flag="$1"
  shift
  [ "$(value "$flag")" = "true" ] || return 1
  require "$@"
}

require WEB_HOSTNAME DASHBOARD_TOKEN DB_SERVER_PASSWORD KEYCLOAK_PASSWORD \
        KEYCLOAK_ADMIN_USER KEYCLOAK_ADMIN_PASSWORD KEYCLOAK_ACCOUNT_DELETION_SECRET CACHE_PASSWORD \
        STORAGE_ACCESSKEY STORAGE_SECRETKEY

require_when SMTP_ENABLED SMTP_HOST SMTP_FROM SMTP_USER SMTP_PASSWORD \
  || echo "::notice::SMTP_ENABLED is not true. Keycloak sends no mail, and the sign-in page offers no password reset."
require_when GOOGLE_SIGN_IN_ENABLED GOOGLE_CLIENT_ID GOOGLE_CLIENT_SECRET \
  || echo "::notice::GOOGLE_SIGN_IN_ENABLED is not true. The login page offers no Google button."

require_when PUSH_ENABLED PUSH_PROJECT_ID FIREBASE_SERVICE_ACCOUNT_BASE64 \
  || echo "::notice::PUSH_ENABLED is not true. Push notification sending is off."

# Plaid credentials can enable bank links even when PLAID_ENABLED is false. The API needs
# BANK_KEY_CERTIFICATE in either case so its stored access tokens remain protected.
require_when PLAID_ENABLED PLAID_CLIENT_ID PLAID_SECRET BANK_KEY_CERTIFICATE \
  || echo "::notice::PLAID_ENABLED is not true. Nobody can link a bank."

if [ "$(value PLAID_ENABLED)" != "true" ] && [ -n "$(value PLAID_CLIENT_ID)" ] \
   && [ -n "$(value PLAID_SECRET)" ] && [ -z "$(value BANK_KEY_CERTIFICATE)" ]; then
  echo "PLAID_CLIENT_ID and PLAID_SECRET are set, so the API links banks whatever PLAID_ENABLED says, but BANK_KEY_CERTIFICATE is not set on the production environment. Set it, or remove the Plaid credentials." >&2
  missing=1
fi

require_when VERYFI_ENABLED VERYFI_CLIENT_ID VERYFI_USERNAME VERYFI_API_KEY \
  || echo "::notice::VERYFI_ENABLED is not true. Receipt transcription through Veryfi is off."

# An explicitly selected provider must also be enabled and fully configured.
provider="$(value RECEIPT_TRANSCRIPTION_PROVIDER)"
case "$provider" in
  AzureOpenAI)
    require_when AZURE_OPENAI_ENABLED AZURE_OPENAI_ENDPOINT AZURE_OPENAI_MODEL AZURE_OPENAI_API_KEY \
      || echo "::notice::AZURE_OPENAI_ENABLED is not true. Azure OpenAI receipt transcription is off."
    ;;
  Veryfi|'') ;;
  *)
    echo "RECEIPT_TRANSCRIPTION_PROVIDER must be Veryfi or AzureOpenAI, not '$provider'" >&2
    missing=1
    ;;
esac

[ "$missing" -eq 0 ]
