# Getting `groupsplit` running

Three things have to be true before a command can work: the binary is on `PATH`, a server is
configured, and there are credentials. `groupsplit config list` answers the middle one, and
`groupsplit auth status` the last.

## Installing

The repository is public and every package the CLI depends on is on nuget.org, so building
it needs nothing but the .NET SDK -- version 10.0.100 or later, pinned in `global.json`.
**This is the path that needs no credentials, and it is the one to reach for first.**

```bash
git clone https://github.com/Sussman-Club/group-split
dotnet run --project group-split/src/GroupSplit.Cli -- groups list --json
```

Everything after `--` is the command line, so `--json` and the rest read exactly as they do
below. For a real `groupsplit` on `PATH` without a feed or a token:

```bash
dotnet pack src/GroupSplit.Cli -c Release -o artifacts/nupkg
dotnet tool install --global --add-source ./artifacts/nupkg --prerelease GroupSplit.Cli
```

`--prerelease` is needed because an untagged local build is versioned
`0.0.0-alpha.0.<commits>` and the installer skips prereleases by default.

### From the org's feed

Releases are published to GitHub Packages, which unlike the repository *is* private, so this
path wants a token with the `read:packages` scope:

```bash
dotnet nuget add source https://nuget.pkg.github.com/Sussman-Club/index.json \
  --name github-sussman --username <github-username> --password <token> \
  --store-password-in-clear-text
dotnet tool install --global GroupSplit.Cli
```

`~/.dotnet/tools` has to be on `PATH`; the installer says so if it is not. `dotnet tool
update --global GroupSplit.Cli` upgrades, and `--prerelease` gets `dev` builds.

**Do not run those two unasked.** `--store-password-in-clear-text` does what it says: the
token lands in plain text in `~/.nuget/NuGet/NuGet.Config`. Building from the checkout above
writes no credential anywhere, which is why it comes first.

### When the tool is missing

A missing `groupsplit` is not a dead end and not a reason to stop. Offer the clone-and-build
above -- it is public, it needs no token, and it produces the same CLI. Ask before installing
anything globally.

## Pointing it at a server

No URL is compiled into the binary. One build talks to a local run, a staging box and
production, and which one is a runtime decision.

A deployment serves the API and Keycloak under a single public origin, so one setting is
usually enough:

```bash
groupsplit config set server https://groupsplit.example.com
```

From that origin the CLI derives the API as `{server}/native/api` and the authority as
`{server}/idp/realms/group-split`. `/native/api` rather than `/api` because they are
different doors: `/api` is the browser's and authenticates with the web app's session
cookie, so a request carrying its own bearer token is refused there.

Sources are consulted in this order, first one wins:

1. `--server`
2. `GROUPSPLIT_SERVER`
3. the `server` key of the active profile in `~/.config/groupsplit/config.json`

When the API and Keycloak are **not** under one origin -- which is every local run, where
each resource gets its own port -- set the two directly. Either replaces the derived value:

```bash
export GROUPSPLIT_API_URL=https://localhost:7043
export GROUPSPLIT_AUTHORITY=http://localhost:8080/realms/group-split
```

`GROUPSPLIT_API_URL` alone is a complete configuration when `GROUPSPLIT_TOKEN` is also set:
nothing on that path contacts the identity server, so nothing needs its URL.

### Every variable the CLI reads

| Variable | |
| --- | --- |
| `GROUPSPLIT_SERVER` | Server origin, as above. |
| `GROUPSPLIT_API_URL` | The API directly, bypassing the derived path. |
| `GROUPSPLIT_AUTHORITY` | The realm directly. |
| `GROUPSPLIT_CLIENT_ID` | The OAuth client. Rarely needed. |
| `GROUPSPLIT_PROFILE` | Select a profile for the whole shell. |
| `GROUPSPLIT_TOKEN` | A bearer token used verbatim. **This is the agent's path.** |
| `GROUPSPLIT_DEBUG` | Any value attaches a stack trace to unexpected errors. |

### Profiles

A profile is one named deployment. `--profile` selects one for a single command;
`GROUPSPLIT_PROFILE` for a shell.

```bash
groupsplit config set server https://staging.example.com --profile staging
groupsplit --profile staging groups list --json
groupsplit config profiles --json
```

The settable keys are `server`, `apiurl`, `authority` and `clientid`. `groupsplit config
list` reports what the *next* command will resolve to and which source each value came
from, which is the quickest way to check before wondering why a request went somewhere
unexpected. `groupsplit config path` prints the file.

## Credentials

### With a token -- the path for agents and CI

```bash
GROUPSPLIT_TOKEN=$(...) groupsplit groups list --json
```

Used verbatim: no device flow, no browser, nothing written to disk. Nothing needs the
authority URL on this path.

`groupsplit auth token` prints the current access token for piping into other tools. **Do
not print it into a transcript.** It is a bearer token for somebody's real money.

### Interactively

`groupsplit auth login` uses the OAuth 2.0 device authorization grant: it prints a URL and a
code, opens a browser when there is one, and waits for approval. The browser that approves
need not be on the same machine, which is why it is the flow used rather than a loopback
redirect.

This is a step only a person can complete. **Do not run it and wait.** When a command
returns exit code 2, tell the user to run `groupsplit auth login`, or ask them for a token
to put in `GROUPSPLIT_TOKEN`.

Credentials land in `~/.local/share/groupsplit/credentials.json`, keyed by realm and client,
narrowed to `0600` on Unix. Expired access tokens refresh silently; a spent refresh token is
discarded so the next command says "sign in" rather than failing the same way twice.

## Against a local stack

Ports are assigned per run, so read them back rather than writing them down:

```bash
json() { aspire describe "$1" --format Json --nologo 2>/dev/null | sed -n '/^{/,$p'; }

export GROUPSPLIT_API_URL=$(json api | jq -r '.resources[0].urls[]|select(.name=="https")|.url')
export GROUPSPLIT_AUTHORITY=$(json keycloak | jq -r '.resources[0].urls[]|select(.name=="http")|.url')/realms/group-split

dotnet run --project src/GroupSplit.Cli -- groups list --json
```

Select the endpoints by name: Keycloak's first URL is its internal management port, not the
one that serves the realm.

A plain-http server that is not loopback warns once on stderr, because the bearer token
crosses the network in the clear. The local Keycloak is http and cannot be otherwise, so the
warning is expected there and is not a reason to stop.
