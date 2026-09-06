# The command line

`groupsplit` is the terminal client for the API. It is built from the same OpenAPI document
as the Blazor and MAUI clients, so its request and response types are the ones in
`GroupSplit.Shared` and it cannot drift from the API's contract.

It is designed to be driven by two kinds of caller at once: a person at a terminal, and a
program -- a script, a CI job, or an AI agent shelling out. Everything below that looks like
a nicety for the second kind is load-bearing.

- [Pointing it at a server](#pointing-it-at-a-server)
- [Signing in](#signing-in)
- [Output](#output)
- [Exit codes](#exit-codes)
- [Errors](#errors)
- [Confirming changes](#confirming-changes)
- [Shell completion](#shell-completion)
- [For agents](#for-agents)
- [Running it against the local stack](#running-it-against-the-local-stack)

## Pointing it at a server

**No URL is compiled into the binary.** One build talks to a local Aspire run, a staging box
and production; which one is a runtime decision.

A deployment serves the API and Keycloak under a single public origin -- the API behind the
web app's `/api` forwarder, Keycloak under `/idp` -- so one setting is enough:

```bash
groupsplit config set server https://groupsplit.example.com
```

From that origin the CLI derives:

| | |
| --- | --- |
| API | `{server}/api` |
| Authority | `{server}/idp/realms/group-split` |

Sources are consulted in this order, first one wins:

1. `--server`
2. `GROUPSPLIT_SERVER`
3. the `server` key of the active profile in `~/.config/groupsplit/config.json`

When the API and Keycloak are *not* under one origin -- which is every local Aspire run,
where each resource gets its own port -- set the two directly instead. Either replaces the
derived value, and setting both means no server origin is needed at all:

```bash
export GROUPSPLIT_API_URL=https://localhost:7043
export GROUPSPLIT_AUTHORITY=http://localhost:8080/realms/group-split
```

`GROUPSPLIT_API_URL` on its own is a complete configuration when `GROUPSPLIT_TOKEN` is also
set: nothing contacts the identity server on that path, so nothing needs its URL. The
authority is only demanded by the commands that actually sign in.

### Profiles

A profile is one named deployment. `--profile` selects one for a single command;
`GROUPSPLIT_PROFILE` selects one for a shell.

```bash
groupsplit config set server https://staging.example.com --profile staging
groupsplit --profile staging groups list
groupsplit config profiles
```

`groupsplit config list` answers the question that actually matters -- what the *next*
command will talk to, after flags, environment and file have all been applied -- and says
which source each value came from.

## Signing in

`groupsplit auth login` uses the OAuth 2.0 device authorization grant (RFC 8628) against
the realm's `cli` client. It prints a URL and a code, opens a browser when there is one, and
waits for approval.

The device flow is used rather than a loopback redirect because a CLI is regularly run where
no browser can be opened and no port can be listened on -- over SSH, in a container, on a
build agent. It degrades to "here is a URL and a code", and the browser that approves it need
not be on the same machine.

Credentials are written to `~/.local/share/groupsplit/credentials.json`, mode `0600`, keyed by
realm and client so several servers can be signed in at once. Expired access tokens are
refreshed silently; a spent refresh token is discarded so the next command says "sign in"
rather than failing the same way twice.

```bash
groupsplit auth login
groupsplit auth status
groupsplit auth logout
```

### Without a browser

Set `GROUPSPLIT_TOKEN` to a bearer token and it is used verbatim: no device flow, no browser,
nothing written to disk. **This is the path for CI and for agents.**

```bash
GROUPSPLIT_TOKEN=$(...) groupsplit groups list
```

`groupsplit auth token` prints the current access token for piping into other tools. It is a
secret; treat it like one.

## Output

Two renderings of the same data, chosen automatically:

| stdout is | Default |
| --- | --- |
| a terminal | text -- tables, colour, sized to the window |
| anything else | JSON |

So a person gets a readable table and `groupsplit groups list \| jq` gets JSON, without either
having to ask. `--json` (or `--output json`) and `--output text` force the choice.

The contract underneath, which everything else here depends on:

- **stdout carries the result and nothing else.** Exit code 0 means stdout is safe to parse.
- **stderr carries everything else** -- progress, warnings, errors. Redirecting stdout to a
  file still leaves the warnings on screen and still produces a clean file.

Colour is off whenever stdout is redirected, when `--no-color` is passed, or when `NO_COLOR`
is set to any value at all.

`--fields` narrows JSON output to the named top-level fields, of an object or of every element
of an array:

```bash
groupsplit groups list --json --fields id,name
```

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success. stdout is trustworthy. |
| `1` | A general failure. |
| `2` | Authentication: not signed in, expired, or refused. |
| `3` | The invocation was wrong: bad usage, a rejected value, or no server configured. |
| `4` | A mutation needs confirming. stdout carries the confirmation envelope. |

The distinction between 1 and 3 is the one worth relying on: **3 means retrying will not help
until something changes.**

Adding a code is a compatible change. Changing what an existing one means is not.

## Errors

Failures print an envelope on stderr. In JSON:

```json
{
  "error": "No group with that id.",
  "code": "GROUP_NOT_FOUND",
  "remediation": "Check the id. List what you can see with: groupsplit groups list",
  "traceId": "00-a1b2c3-d4e5f6-01"
}
```

`code` is stable and is what a caller should branch on; `error` is prose and may be reworded.
When the failure came from the API, `code` is that response's code verbatim, from the catalog
in [errors.md](errors.md) -- the CLI does not invent a second vocabulary for conditions the
API already names. Codes the CLI raises before any request was made are prefixed `CLI_`, so
the prefix tells you whether the server ever saw the request.

`traceId` is the API's own, and matches its log line.

## Confirming changes

Destructive commands stop unless confirmed.

At a terminal you get a prompt. **Everywhere else** -- a pipe, CI, an agent -- you get exit
code 4 and a description of what would have happened on stdout:

```json
{
  "confirmationRequired": true,
  "action": "groups.leave",
  "summary": "Leave the group 'Trip to Lisbon'?",
  "changes": ["You are removed from 'Trip to Lisbon' (4 members)."],
  "confirmCommand": "groupsplit groups leave 3f25... --yes"
}
```

`confirmCommand` is the exact string that performs the action, so a caller with no terminal
does not have to reconstruct the invocation -- it re-runs that, or shows it to a human first.
`--yes` skips the gate.

Nothing ever blocks on a prompt that nobody can answer.

## Shell completion

Completion is answered by the parser itself, so a new subcommand completes without anyone
maintaining a list of words.

```bash
groupsplit completion bash > /etc/bash_completion.d/groupsplit
groupsplit completion zsh  > "${fpath[1]}/_groupsplit"
groupsplit completion fish > ~/.config/fish/completions/groupsplit.fish
groupsplit completion pwsh >> $PROFILE
```

## For agents

Four things make this usable by a model without a human in the loop:

1. **`groupsplit schema --json`** prints the whole command tree -- every command, flag, type,
   arity and the exit-code table -- in one call. It is generated from the same tree that parses
   the arguments, so it cannot describe a CLI that does not exist. Read it once instead of
   shelling out to `--help` per subcommand and parsing prose.
2. **JSON is the default when not a terminal**, so no flag has to be remembered.
3. **`GROUPSPLIT_TOKEN`** authenticates with no browser and no interactive step.
4. **Exit code 4 with `confirmCommand`** means a destructive action can be proposed and shown
   to a human rather than either blocking forever or being performed unasked.

A reasonable loop is: read `schema` once, run commands with `--json`, branch on the exit code,
and on 4 surface `summary` and `changes` before re-running `confirmCommand`.

Set `GROUPSPLIT_DEBUG=1` to attach a stack trace to the envelope of an unexpected error. An
envelope with code `CLI_INTERNAL_ERROR` is always a defect in the CLI, not a usage mistake.

## Running it against the local stack

The AppHost registers the CLI as an explicit-start resource, so `WithReference` hands it this
run's API and realm URLs and its requests appear in the dashboard's traces beside the API
spans that served them. Start it from the dashboard when you want that.

For actually working on it, run it directly and let the environment point it at the stack:

```bash
export GROUPSPLIT_API_URL=$(aspire describe api --output json | jq -r '.endpoints[0].url')
dotnet run --project src/GroupSplit.Cli -- groups list
```

The `cli` Keycloak client lives in `src/GroupSplit.AppHost/Assets/keycloak/realms.json`. It is
public, device-grant only, and carries the same `api` audience mapper as `web-app` -- without
that mapper the API rejects its tokens for the wrong audience.
