#!/usr/bin/env bash
set -euo pipefail

# Update the existing Komodo stack from the generated Compose files. The workflow supplies
# the credentials and GitHub metadata.

output_dir="${COMPOSE_OUTPUT_DIR:-src/GroupSplit.AppHost/aspire-output}"
classify="$GITHUB_WORKSPACE/.github/scripts/classify-komodo-failure.sh"
docs="$GITHUB_SERVER_URL/$GITHUB_REPOSITORY/blob/$GITHUB_SHA/docs/deployment.md"
cd "$output_dir"

# The exit trap uses this state when an unhandled command fails.
doing='preparing the Komodo update'
touched='no'
update_id=''
summarised=''

# Write a failed-deploy summary for the GitHub job.
summarise() {
  {
    echo "### $1"
    echo
    echo "- Release: \`$DEPLOYMENT_VERSION\`"
    echo "- Commit: \`$DEPLOYMENT_COMMIT\`"
    [ -z "$update_id" ] || echo "- Komodo update: \`$update_id\`"
    echo "- Failed at: $2"
    echo "- Production touched: $3"
    echo "- Outcome: $4"
    [ -z "${5:-}" ] || printf '\n%s\n' "$5"
  } >> "$GITHUB_STEP_SUMMARY"
  summarised=1
}

# These files contain production secrets. Remove them before GitHub runs action post-steps.
finish() {
  local code=$?
  rm -f payload.json previous.json rollback.json .env.production
  if [ "$code" -ne 0 ] && [ -z "$summarised" ]; then
    summarise "Deployment failed" "$doing" "$touched" "the step stopped there; its log says why"
  fi
}
trap finish EXIT

# Aspire may generate parameter values that GitHub does not know to mask.
while IFS= read -r line || [ -n "$line" ]; do
  case "$line" in ''|'#'*) continue ;; esac
  key="${line%%=*}"
  value="${line#*=}"
  value="${value#\"}"
  value="${value%\"}"
  if [[ "${key^^}" =~ PASSWORD|SECRET|KEY|TOKEN|CERT ]] && [ "${#value}" -ge 4 ]; then
    echo "::add-mask::$value"
  fi
done < .env.production

auth=(-H "X-Api-Key: $KOMODO_API_KEY" -H "X-Api-Secret: $KOMODO_API_SECRET" -H 'Content-Type: application/json')
# Reads and UpdateStack are safe to retry.
curl_args=(--fail-with-body --silent --show-error --retry 5 --retry-delay 2 --retry-all-errors --connect-timeout 10 --max-time 60)

# Do not retry DeployStack: a slow or lost response may have already started it.
execute_args=(--fail-with-body --silent --show-error --connect-timeout 10 --max-time 120)

deploy() {
  local response id
  response=$(curl "${execute_args[@]}" -X POST "$KOMODO_URL/execute" "${auth[@]}" \
    -d "{\"type\":\"DeployStack\",\"params\":{\"stack\":\"$STACK\"}}")
  id=$(jq -r '._id."$oid" // empty' <<<"$response")
  if [ -z "$id" ]; then
    echo "Komodo did not return an update id." >&2
    return 1
  fi
  printf '%s' "$id"
}

# /execute only accepts the request. Keep the latest update response in $status.
wait_for() {
  local id="$1" state=''
  for _ in $(seq 1 60); do
    sleep 5
    status=$(curl "${curl_args[@]}" -X POST "$KOMODO_URL/read" "${auth[@]}" \
      -d "{\"type\":\"GetUpdate\",\"params\":{\"id\":\"$id\"}}")
    state="$(jq -r '.status // empty' <<<"$status")"
    [ "$state" = "Complete" ] && return 0
  done
  echo "::error::Komodo update $id was still '${state:-unknown}' after five minutes. It is probably still running on the host; check the stack there before retrying, because a second deploy would race it."
  return 1
}

# Do not print Compose Config stdout: it contains the interpolated Compose file and secrets.
report_failure() {
  local service
  jq -r '(.logs // [])[] | select(.success == false)
         | "--- \(.stage): \(.command)\n\(.stderr // "" | gsub("<[^>]+>"; ""))"' <<<"$status"
  for service in $(jq -r --arg stack "$STACK" '
      [(.logs // [])[] | select(.success == false) | .stderr
       | (scan("service \"([^\"]+)\" didn.t complete successfully") | .[0]),
         (scan("container " + $stack + "-(.+?)(?:-[0-9]+)? is unhealthy") | .[0])]
      | unique | .[]' <<<"$status"); do
    echo "::group::Log of $service"
    curl "${curl_args[@]}" -X POST "$KOMODO_URL/read" "${auth[@]}" \
      -d "{\"type\":\"GetStackLog\",\"params\":{\"stack\":\"$STACK\",\"services\":[\"$service\"],\"tail\":200}}" \
      | jq -r '.stdout, .stderr' \
      || echo "Could not read the log of $service from Komodo."
    echo "::endgroup::"
    echo "::error::Service $service did not complete successfully; its log is in the group above."
  done
}

failed_stages() {
  bash "$classify" <<<"$status" \
    | jq -r '.failed_stages | if length == 0 then "no stage Komodo recorded" else join(", ") end'
}

# Save the current stack config for rollback. If this read fails, deploy anyway but do not
# claim a rollback succeeded later.
if curl "${curl_args[@]}" -X POST "$KOMODO_URL/read" "${auth[@]}" \
     -d "{\"type\":\"GetStack\",\"params\":{\"stack\":\"$STACK\"}}" > previous.json \
   && jq -e '(.config.file_contents // "") != "" and (.config.environment // "") != ""' previous.json > /dev/null; then
  jq --arg stack "$STACK" '{
    type: "UpdateStack",
    params: { id: $stack,
              config: { file_contents: .config.file_contents, environment: .config.environment,
                        registry_provider: .config.registry_provider,
                        registry_account: .config.registry_account } }
  }' previous.json > rollback.json
else
  echo "::warning::Could not read the current configuration of stack $STACK, so a failed deploy will not be rolled back."
fi
rm -f previous.json

# Let jq encode the YAML and environment file.
jq -n --arg stack "$STACK" \
      --arg registry_account "$KOMODO_REGISTRY_ACCOUNT" \
      --rawfile yaml docker-compose.yaml \
      --rawfile env .env.production '{
  type: "UpdateStack",
  params: { id: $stack,
            config: { file_contents: $yaml, environment: $env,
                      registry_provider: "ghcr.io",
                      registry_account: $registry_account } }
}' > payload.json
doing='writing the release into the Komodo stack'
curl "${curl_args[@]}" -X POST "$KOMODO_URL/write" "${auth[@]}" --data @payload.json > /dev/null

status=''
doing='starting the Komodo deploy'
touched="unknown: check the stack's updates in Komodo"
update_id=$(deploy)
doing="waiting for Komodo update $update_id"
touched='unknown: the update is probably still running on the host'
wait_for "$update_id"

if ! jq -e '.success == true' <<<"$status" > /dev/null; then
  echo "::error::Komodo deploy failed for update $update_id."
  report_failure

  classification="$(bash "$classify" <<<"$status")"
  kind="$(jq -r '.kind' <<<"$classification")"
  failed="$(failed_stages)"

  if [ "$kind" = registry-login ]; then
    # Komodo has not pulled or restarted anything. Restore the config, but do not redeploy.
    if [ -f rollback.json ] \
       && curl "${curl_args[@]}" -X POST "$KOMODO_URL/write" "${auth[@]}" --data @rollback.json > /dev/null; then
      outcome="not rolled back, because nothing changed; Komodo's stack configuration is the previous one again"
    else
      outcome="not rolled back, because nothing changed; Komodo's stack still holds this release's configuration, which is not what runs"
    fi

    echo "::error::Komodo could not log in to ghcr.io as its registry account '$KOMODO_REGISTRY_ACCOUNT', so it stopped before pulling or restarting anything. Production was not touched and still runs the previous release. Not rolled back: a redeploy starts with the same login. Renew that account's GHCR token in Komodo, then re-run this job."
    summarise "Deployment failed: Komodo could not log in to GHCR" "$failed" \
      "no, Komodo stops at the login, before it pulls or restarts anything" "$outcome" \
      "**To fix:** the GHCR credential of Komodo's registry account \`$KOMODO_REGISTRY_ACCOUNT\` was refused, most likely because its token expired. Renew it in Komodo (Settings -> Providers -> ghcr.io) with a classic personal access token that has \`read:packages\`, then re-run this job. See [the registry account Komodo pulls with]($docs#the-registry-account-komodo-pulls-with)."
    exit 1
  fi

  case "$kind" in
    other) touched='possibly: the update got past the registry login, and Compose may have recreated containers' ;;
    *) touched='unknown, Komodo recorded no stage' ;;
  esac

  if [ ! -f rollback.json ]; then
    summarise "Deployment failed" "$failed" "$touched" \
      "not rolled back: the previous configuration could not be read before the deploy"
    exit 1
  fi

  echo "Restoring the previous configuration of stack $STACK and deploying it again."
  doing="rolling back after $failed failed"
  curl "${curl_args[@]}" -X POST "$KOMODO_URL/write" "${auth[@]}" --data @rollback.json > /dev/null
  rollback_id=$(deploy)
  doing="waiting for the rollback, Komodo update $rollback_id"
  wait_for "$rollback_id"

  if jq -e '.success == true' <<<"$status" > /dev/null; then
    echo "::error::Rolled back: production runs the previous stack configuration again (Komodo update $rollback_id)."
    outcome="rolled back to the previous configuration (\`$rollback_id\`)"
  else
    echo "::error::The rollback failed as well (Komodo update $rollback_id), so production is probably down."
    report_failure
    outcome="failed, and so did the rollback (\`$rollback_id\`, at $(failed_stages)), so production is probably down"
  fi

  summarise "Deployment failed" "$failed" "$touched" "$outcome"
  exit 1
fi

{
  echo "### Deployment"
  echo
  echo "- Release: \`$DEPLOYMENT_VERSION\`"
  echo "- Commit: \`$DEPLOYMENT_COMMIT\`"
  echo "- Komodo update: \`$update_id\`"
  echo "- Environment: \`production\`"
} >> "$GITHUB_STEP_SUMMARY"
