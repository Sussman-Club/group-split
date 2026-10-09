#!/usr/bin/env bash
set -euo pipefail

# Reads a failed Komodo update -- the GetUpdate response the deploy job polls -- on stdin
# and says what kind of failure it was, as one line of JSON:
#
#   {"kind":"registry-login","failed_stages":["Login to Registry"],"production_touched":"no"}
#
# kind is one of:
#   registry-login     Komodo could not log in to the image registry. It stops there, before
#                      it pulls or restarts anything, and redeploying the previous configuration
#                      starts with the same login -- so a rollback could only fail the same way.
#   before-containers  Every stage the update reached is one that runs before Komodo changes a
#                      container (validating the Compose file, pulling images), so the stack
#                      still runs what it ran before.
#   containers         The update reached a stage that can change containers -- Compose Up, a
#                      deploy hook, or a stage this script does not know -- so production may be
#                      down or half-changed.
#   unknown            The update records no stages at all.
#
# A stage this script has never seen counts as one that changes containers. Being wrong in
# that direction costs a rollback that was not needed, which is what every failure got before
# this script existed; being wrong in the other would skip one that was.

jq -c '
  # Stages Komodo runs before it touches a container. Matched by name, ignoring case.
  def before_containers:
    test("^(login to registry|compose (config|pull|build)|(write|clone|pull) .+|interpolate .+)$"; "i");

  (.logs // []) as $logs
  | [$logs[] | select(.success == false) | .stage // ""] as $failed
  | if any($failed[]; test("^login to registry$"; "i")) then
      { kind: "registry-login", production_touched: "no" }
    elif ($logs | length) == 0 then
      { kind: "unknown", production_touched: "unknown" }
    elif all($logs[]; .stage // "" | before_containers) then
      { kind: "before-containers", production_touched: "no" }
    else
      { kind: "containers", production_touched: "yes" }
    end
  | { kind, failed_stages: $failed, production_touched }
'
