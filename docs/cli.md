# The command line

`groupsplit` is the terminal client for the API. It is built from the same OpenAPI document
as the Blazor and MAUI clients, so its request and response types are the ones in
`GroupSplit.Shared` and it cannot drift from the API's contract.

It is designed to be driven by two kinds of caller at once: a person at a terminal, and a
program -- a script, a CI job, or an AI agent shelling out. Everything below that looks like
a nicety for the second kind is load-bearing.

- [What it covers](#what-it-covers)
- [Installing it](#installing-it)
- [Keeping it updated](#keeping-it-updated)
- [Pointing it at a server](#pointing-it-at-a-server)
- [Signing in](#signing-in)
- [Bank sync](#bank-sync)
- [Editing an expense](#editing-an-expense)
- [Output](#output)
- [Exit codes](#exit-codes)
- [Errors](#errors)
- [Confirming changes](#confirming-changes)
- [Shell completion](#shell-completion)
- [For agents](#for-agents)
- [Running it against the local stack](#running-it-against-the-local-stack)

## What it covers

Every endpoint the API has. There is no read-only subset and nothing the web app can do that
this cannot, which is the property worth keeping: a command missing here is a thing a script
has to reach for `curl` and a bearer token to do.

| | |
| --- | --- |
| `auth` | `login`, `logout`, `status`, `token` |
| `groups` | `list`, `show`, `create`, `rename`, `members`, `remove-member`, `balances`, `settle`, `activity`, `archive`, `unarchive`, `leave`, `invite`, `invitations`, `withdraw-invitation`, `link show\|create\|revoke` |
| `transactions` (`tx`) | `list`, `show`, `create`, `update`, `summary`, `shares list\|summary`, `bank-matches`, `delete` |
| `categories` | `list`, `create`, `update`, `delete` |
| `split-rules` | `list`, `show`, `create`, `update`, `delete` |
| `invitations` | `list`, `accept`, `decline`, `link`, `join` |
| `bank` | `list`, `link-token`, `link`, `sync`, `unlink` |
| `inbox` | `list`, `summary`, `matches`, `file`, `link`, `dismiss-match`, `ignore`, `restore` |
| `users` | `me`, `position`, `delete` |
| `config` | `list`, `get`, `set`, `unset`, `profiles`, `path` |

The flags of each are in `groupsplit <command> --help`, and all of them at once in
`groupsplit schema --json` -- which is generated from the same tree that parses the
arguments, so this table can go stale and that one cannot.

Two commands read the group's roster or listing before writing, so a confirmation can name
what it is about to change rather than echo a guid back: `groups remove-member`,
`groups settle`. `bank unlink`, `categories delete` and `split-rules delete` do the same.
`groups link create` reads for a further reason: it only asks when the group already has a
link to lose, and making a group's first one destroys nothing.

## Installing it

The CLI is published to **GitHub Packages**, private to the org, so installing it needs a
token and nothing else -- no checkout, no build, no branch to be on.

### One-time: point NuGet at the feed

You need a GitHub personal access token (classic) with the **`read:packages`** scope, and
membership of the org.

```bash
dotnet nuget add source https://nuget.pkg.github.com/Sussman-Club/index.json \
  --name github-sussman \
  --username <your-github-username> \
  --password <token> \
  --store-password-in-clear-text
```

`--store-password-in-clear-text` is required on Linux and macOS, which have no NuGet
encryption provider. It does what it says: the token lands in plain text in
`~/.nuget/NuGet/NuGet.Config`. Give it `read:packages` and nothing more, and expect to
replace it when it expires.

### Then

```bash
dotnet tool install --global GroupSplit.Cli
groupsplit --help
```

`~/.dotnet/tools` has to be on `PATH`; the installer says so if it is not. Removing it is
`dotnet tool uninstall --global GroupSplit.Cli`.

### How releases happen

Nothing to do. Merging a CLI change publishes the CLI, the same way merging deploys the stack,
so the installed tool cannot quietly lag behind what was merged and there is no release step to
remember. A merge that leaves the CLI alone publishes nothing -- see [When nothing is
published](#when-nothing-is-published).

Both branches publish, and the version says which is which:

| Merged to | Publishes | Who gets it |
| --- | --- | --- |
| `main` | `0.0.4` | everyone, on `dotnet tool update` |
| `dev` | `0.0.5-dev.213` | only `--prerelease` |

So `dev` is installable without being what anyone gets by accident:

```bash
dotnet tool update --global GroupSplit.Cli --prerelease
```

`.github/workflows/release-cli.yml` runs the tests, works out the version, packs and
publishes.

### What the numbers do

The first release is `0.0.1` and each push to `main` takes the next patch. The workflow claims
one by creating a `cli-v*` tag, which MinVer then reads, so every released version points at
the commit that produced it.

A `dev` build is a prerelease of the patch `main` will release next -- `0.0.5-dev.213` where
213 is the commit count. It sorts below `0.0.5` and above `0.0.5-dev.212`, so dev builds order
among themselves and can never shadow a release.

That version is computed rather than left to MinVer for a reason worth knowing before changing
it: a release tag lives on `main`'s merge commit, which is **not an ancestor of `dev`**. MinVer
would never see it, and every dev build would claim `0.0.0-alpha` while main sat at `0.0.5`.
Reading the tag list directly ignores reachability and keeps the two lines in step.

Three more consequences, all deliberate:

- **A re-run does not consume a version.** On `main` a tag already on the commit is reused, so
  re-running a failed publish packs the same version and the push is a no-op.
- **A deliberate bump is a manual tag.** Tag `cli-v0.1.0` yourself and push it before the
  merge; the workflow reuses it, and the next automatic version is `0.1.1`. That is the only
  reason to touch a tag by hand.
- **The workflow refuses a version that does not suit its branch** -- a prerelease from `main`,
  or a release version from `dev`. Both mistakes are otherwise silent, and the second would
  burn a number `main` could then never use.

Versions built locally are `0.0.<next>-alpha.0.<height>`, which is why the local install below
needs `--prerelease` too.

### From the checkout instead

Still works, and is the better option while you are changing the CLI itself, since it skips
the tag-and-publish round trip:

```bash
dotnet pack src/GroupSplit.Cli -c Release -o artifacts/nupkg
dotnet tool install --global --add-source ./artifacts/nupkg --prerelease GroupSplit.Cli
```

`--prerelease` is needed here because an untagged build is versioned
`0.0.0-alpha.0.<commits>`, and the installer skips prereleases by default.

## Keeping it updated

```bash
dotnet tool update --global GroupSplit.Cli
```

That is the whole thing once the feed is configured. `groupsplit --version` prints the
version and the commit it was built from, so an installed tool can always be traced back to
what produced it.

### Why the version is not written by hand

`dotnet tool update` compares versions and does nothing when they match. A hand-written
`<Version>` is the same on every build, so publishing a change without remembering to bump it
would leave the **old binary installed** while reporting success -- a stale tool that gives no
sign of being stale. Publishing on every merge makes that failure mode routine rather than
occasional, which is why the version comes from git and the workflow claims a new one every
time the CLI changes.

Both CI and the release workflow check out with `fetch-depth: 0`, because MinVer needs the
tags and the history to derive it.

### When nothing is published

Most commits here are API or app work that leaves the packed tool byte-identical. Publishing
those anyway spent a version number per merge and told anyone reading the feed that something
had changed when nothing had, so the workflow only triggers on a push that touches the CLI.

The `paths:` filter at the top of `.github/workflows/release-cli.yml` is the whole definition:
`src/GroupSplit.Cli`, `src/GroupSplit.Shared`, `src/GroupSplit.API`, `global.json`, and the
workflow itself. `GroupSplit.API` is in there whole even though the CLI consumes only its
OpenAPI document, because any endpoint change regenerates the client and narrowing the list to
controllers would stop triggering the day someone moves a file. The tests are deliberately
absent -- a test-only change does not alter the shipped tool -- but they still run, and still
gate the publish, whenever the workflow does fire.

One caveat, worth knowing before you go looking for a missing version: the filter judges each
push on its own. If a CLI change's run fails or is cancelled, the *next* push will not pick it
up, because that push has no CLI change of its own to trigger on -- the release sits unpublished
and nothing goes red. Re-run the workflow by hand (`workflow_dispatch`) if that happens; a
manual run ignores the filter.

### Without installing

Nothing has to be installed to run it:

```bash
dotnet run --project src/GroupSplit.Cli -- groups list
```

### Why it is not a local tool

`dotnet-tools.json`, alongside `nswag` and `dotnet-ef`, looks like the natural home -- one
pinned version everyone shares. It is deliberately not there.

The manifest records a package id and a version but never where to get them, so
`dotnet tool restore` resolves against whatever sources the machine has configured. Adding
this package would make that restore **fail for every contributor without a `read:packages`
token**, including those who never touch the CLI, turning a working `git clone` into one that
needs credentials first. A global tool keeps that cost on the people who want the tool.

### Standalone binaries

`dotnet publish -r <rid>` works today if a tool package is the wrong shape. Native AOT does
not -- the generated client binds through reflection-based System.Text.Json and would need a
`JsonSerializerContext` first.

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
| API | `{server}/native/api` |
| Authority | `{server}/idp/realms/group-split` |
| Join links | `{server}/join/{token}` |

The last of those is the only place the origin is used for something other than reaching a
service. A group's join link is a URL somebody opens in a browser, and the API does not know
where the app is published -- so `groupsplit groups link show` composes it here, and without
a server origin can only give you the token and say why.

`/native/api` rather than `/api` because they are different doors. `/api` is the browser's:
it authenticates with the web app's session cookie and swaps in the token held inside it, so
a request carrying its own token is refused there. `/native/api` forwards to the same API and
leaves the Authorization header alone, which is what a client holding a token needs -- this
CLI, and the MAUI app.

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

### Plain http

Accepted, and necessarily so -- the local Aspire Keycloak is http and cannot be otherwise.
A server that is not loopback warns once on stderr, because over http the bearer token, and on
the authority the refresh token, cross the network in the clear.

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

Two things the sign-in refuses to do, both because the identity server names the addresses this
CLI would otherwise trust with a device code and a refresh token:

- **It will not send credentials off the authority's origin.** Every endpoint in the realm's
  discovery document -- issuer, token, device authorization, logout -- has to sit on the same
  origin as the authority you configured, which also rules out an https authority whose
  document names an http endpoint. Nothing legitimate is turned away: Keycloak serves its
  endpoints under its own origin.
- **It will not open a verification address that is not an http or https URL.** Opening one
  goes through the desktop's handler, which will just as happily run an executable or a UNC
  path, and that address came off the wire.

Credentials are written to `~/.local/share/groupsplit/credentials.json`, keyed by realm and
client so several servers can be signed in at once. On Unix the file is narrowed to `0600`
before anything is written to it; on Windows it inherits the permissions of `%APPDATA%`, which
is already user-scoped. Expired access tokens are
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

## Bank sync

Two halves, and only one of them can happen in a terminal.

Linking a bank goes through the provider's own UI -- a browser and a JavaScript SDK, opened
with a short-lived token. That part is the web app's. What is here is the token, and the
exchange afterwards:

```bash
groupsplit bank link-token                 # hand this token to the provider's UI
groupsplit bank link <public-token>        # exchange what it gave back
```

`--connection <id>` on `link-token` asks for an *update* token, which reopens an existing
connection rather than adding a second copy of the same bank. That is what a connection
showing `login required` needs; `groupsplit bank list` says which ones do.

Everything after linking is ordinary:

```bash
groupsplit bank sync <connection-id>       # queued, not awaited: a full history takes a while
groupsplit inbox list                      # what arrived
groupsplit inbox file <row-id> --group <group-id> --category-id <category-id>
groupsplit inbox ignore <row-id>           # undone by: inbox restore
```

`bank sync` answers as soon as the pull is queued, because nothing waits on a bank inside a
request. `groupsplit inbox summary` is the cheap way to poll for it -- it is one count, not a
page of rows.

Filing copies what the bank said. The date, the amount and the currency have no flags on
`inbox file` for that reason: correcting the bank is an edit to the expense afterwards.

When the deployment has no provider configured, `bank list` says so rather than answering
with an empty list -- "no banks linked" and "bank sync is off here" are different answers.

### When the row is already an expense

Somebody records the dinner at the table so nobody forgets it. The card charge lands two days
later. Filing it would put the same dinner in the group twice, and
[the rule that catches that](duplicate-detection.md) is on the API, so it catches this client
too:

```bash
groupsplit inbox file <row-id>
# error: This looks like an expense you have already recorded.
#   - 3f25... Dinner 40.00 GBP in The flat, 2 days apart, 6.00 apart
```

Exit code 3, and **no second expense exists** -- the refusal comes before one is written. The
`details` of the envelope carry each expense it matched, id first, because the id is the
argument two of the three answers take:

```bash
groupsplit inbox link <row-id> <transaction-id>            # it is the same payment
groupsplit inbox file <row-id> --file-anyway               # you really did pay twice
groupsplit inbox dismiss-match <row-id> <transaction-id>   # not a match at all
```

`inbox link` leaves one expense with the bank's row attached. The amount, the date and the
division are untouched: a card settling for six more than the receipt is not a correction
anybody asked for. `dismiss-match` means the pair is never suggested again.

Nothing has to wait for the refusal. `inbox list` marks a row `(duplicate?)` and counts them
underneath, and `groupsplit inbox matches <row-id>` names what it matched and why -- how many
days apart and how far the amounts are -- without filing anything.

The other order is asked from the other end. Somebody who typed the expense first wants to
know when the card charge arrives, and the row is what they need the id of:

```bash
groupsplit transactions bank-matches <transaction-id>
```

Both directions answer with the same two commands. Which of the three applies is a question
about the money, so no default is picked: `--file-anyway` is never on unless it is asked for,
because the refusal is the whole mechanism that stops the second expense coming into being
before somebody has been told about the first.

## What you owe, not what you paid

`transactions list` answers one question -- rows where you are the payer -- and
`transactions shares` answers the other one, which nothing could answer before: the
expenses you owe a part of, whoever paid for them.

```bash
groupsplit tx shares list                        # newest first
groupsplit tx shares list --sort-by share        # what is costing you the most
groupsplit tx shares list --group <group-id> --from 2026-01-01
groupsplit tx shares summary
```

Every row carries two amounts, because they are two different numbers and only one of them
is yours: `amount` is what the whole expense came to and `share` is your part of it. The
same filters, sorts and paging as `transactions list`, plus `share` as a sort key.

```
Id      Date        Name    Total   Your share  Paid by  Group
3f25…   2026-02-14  Dinner  90.00   45.00       Omar     The flat
a91c…   2026-02-13  Taxi    30.00   15.00       you      The flat
```

An expense you paid for **and** owe a share of is in the listing and marked `you`, because
leaving it out would make the listing disagree with the group's own figures. It is kept out
of one number and only one: `shares summary` reports your share of everything and, beside
it, the part of that sitting on somebody else's expense.

```bash
groupsplit tx shares summary
# 3 expenses totalling 124.00, your share 64.00
# Of that, 45.00 is on expenses somebody else paid.
```

Your share of your own dinner is money you already have -- you are owed the rest of it --
so `64.00` is not a debt and `45.00` is. Both are gross: a settlement is a transfer rather
than an expense, so nothing here has been paid back yet. Where you actually stand is
`groupsplit users position`.

## Editing an expense

`transactions update` sends a JSON Patch of **only** the flags you passed, and that is
load-bearing rather than an optimisation. The API reads the patch as well as applying it:
saying nothing about the shares means "divide it again the way the category says", which is
what an edit to the amount, the payer or the category should do. Naming them with `--split`
means those exact amounts, checked against the total.

```bash
groupsplit tx update <id> --amount 46.00                      # shares are recomputed
groupsplit tx update <id> --split <user-id>=30.00 --split <user-id>=16.00
groupsplit tx update <id> --personal                          # take it out of its group
groupsplit tx update <id> --description ""                    # clear the note
```

So a command that names nothing is refused (exit code 3) rather than sent as an empty patch,
which the server would accept as a successful no-op.

`--group` and `--personal` contradict each other, as do `--category-id` and `--no-category`;
either pair is refused before anything is sent.

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
# Ports are assigned per run, so read them back rather than writing them down. Select the
# endpoints by name: Keycloak's first URL is its internal management port, not the one
# that serves the realm.
json() { aspire describe "$1" --format Json --nologo 2>/dev/null | sed -n '/^{/,$p'; }

export GROUPSPLIT_API_URL=$(json api | jq -r '.resources[0].urls[]|select(.name=="https")|.url')
export GROUPSPLIT_AUTHORITY=$(json keycloak | jq -r '.resources[0].urls[]|select(.name=="http")|.url')/realms/group-split

dotnet run --project src/GroupSplit.Cli -- groups list
```

`groupsplit config list` echoes back what it resolved, which is the quickest way to check
those two before wondering why a request went somewhere unexpected.

### The `cli` Keycloak client

It lives in `src/GroupSplit.AppHost/Assets/keycloak/realms.json`: public, device-grant only,
and carrying the same `api` audience mapper as `web-app` -- without that mapper the API
rejects its tokens for the wrong audience.

**A realm that already exists will not pick it up.** Keycloak imports a realm on first start
only, and both the local Keycloak (`WithDataVolume`) and the deployed one keep theirs, so
adding a client to this file does nothing for an environment that has already run. Either add
the client through the admin console, or -- locally, where the data is disposable -- drop the
volume and let the realm be imported again:

```bash
aspire stop
podman volume ls | grep keycloak     # confirm the name before removing anything
podman volume rm <the keycloak data volume>
aspire run
```

That loses locally signed-up accounts; `groupsplit auth login` works afterwards, and the
seeder's **Reset databases and seed** command puts the demo accounts back.

Until then, `GROUPSPLIT_TOKEN` needs no realm change at all.
