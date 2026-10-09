#!/usr/bin/env bash
set -euo pipefail

# Runs classify-komodo-failure.sh over the sample Komodo updates in testdata/ and checks what
# it says about each. Needs bash and jq, which every GitHub runner has:
#
#   bash .github/scripts/classify-komodo-failure.test.sh
#
# The two *-run-37843928725.json samples are rebuilt from that deploy run's log: attempt 1
# failed on an expired GHCR token, attempt 3 on an unhealthy service after Compose Up. The
# log shows only the failed stage, so those samples hold only that stage, and the registry
# account's name is replaced with a placeholder. compose-pull.json is written by hand: a
# login that succeeded, then a later stage that failed, which must not read as a failed
# login.

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
failures=0
cases=0

# expect SAMPLE KIND TOUCHED FAILED_STAGES: FAILED_STAGES is the failed stage names joined
# with ", ", or empty.
expect() {
  local sample="$1" kind="$2" touched="$3" stages="$4" actual expected
  cases=$((cases + 1))
  actual="$(bash "$here/classify-komodo-failure.sh" < "$here/testdata/$sample" \
    | jq -r '[.kind, .production_touched, (.failed_stages | join(", "))] | join(" | ")')"
  expected="$kind | $touched | $stages"
  if [ "$actual" = "$expected" ]; then
    echo "ok   $sample"
  else
    echo "FAIL $sample: expected '$expected', got '$actual'"
    failures=$((failures + 1))
  fi
}

expect registry-login-run-37843928725.json registry-login no "Login to Registry"
expect compose-up-run-37843928725.json other possibly "Compose Up"
expect compose-pull.json other possibly "Compose Pull"
expect no-logs.json unknown unknown ""

# Not a GetUpdate response at all: the script must fail rather than invent a verdict.
cases=$((cases + 1))
if printf 'not json' | bash "$here/classify-komodo-failure.sh" > /dev/null 2>&1; then
  echo "FAIL malformed input was classified instead of rejected"
  failures=$((failures + 1))
else
  echo "ok   malformed input is rejected"
fi

echo "$((cases - failures)) of $cases passed."
[ "$failures" -eq 0 ]
