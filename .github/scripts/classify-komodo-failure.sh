#!/usr/bin/env bash
set -euo pipefail

# Reads a failed Komodo update -- the GetUpdate response the deploy job polls -- on stdin
# and says what kind of failure it was, as one line of JSON:
#
#   {"kind":"registry-login","failed_stages":["Login to Registry"],"production_touched":"no"}
#
# kind is one of:
#   registry-login  Komodo could not log in to the image registry. It stops there, before it
#                   pulls or restarts anything, and redeploying the previous configuration
#                   starts with the same login -- so a rollback could only fail the same way.
#   other           The update failed somewhere after the login. Compose may have recreated
#                   containers by then, so the job rolls back as it always has.
#   unknown         The update records no stages at all.
#
# Only the registry login is told apart, because it is the one stage seen failing for real
# (run 37843928725) and known to leave production as it was. Other stages that probably run
# before any container changes are not singled out: their names have not been seen in a
# real update, and guessing wrong would skip a rollback that was needed.

jq -c '
  (.logs // []) as $logs
  | [$logs[] | select(.success == false) | .stage // ""] as $failed
  | if any($failed[]; test("^login to registry$"; "i")) then
      { kind: "registry-login", production_touched: "no" }
    elif ($logs | length) == 0 then
      { kind: "unknown", production_touched: "unknown" }
    else
      { kind: "other", production_touched: "possibly" }
    end
  | { kind, failed_stages: $failed, production_touched }
'
