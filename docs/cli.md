# The command line

`groupsplit` is the terminal client for the API. It is built from the same OpenAPI document
as the Blazor and MAUI clients, so its request and response types are the ones in
`GroupSplit.Shared` and it cannot drift from the API's contract.

It is designed to be driven by two kinds of caller at once: a person at a terminal, and a
program -- a script, a CI job, or an AI agent shelling out. Everything below that looks like
a nicety for the second kind is load-bearing.

- [What it covers](#what-it-covers)
- [What each command does](#what-each-command-does)
- [Installing it](#installing-it)
- [Keeping it updated](#keeping-it-updated)
- [Pointing it at a server](#pointing-it-at-a-server)
- [Signing in](#signing-in)
- [Amounts](#amounts)
- [Output](#output)
- [Exit codes](#exit-codes)
- [Errors](#errors)
- [Confirming changes](#confirming-changes)
- [Shell completion](#shell-completion)
- [For agents](#for-agents)
- [Running it against the local stack](#running-it-against-the-local-stack)

## What it covers

Every endpoint the API has. There is no read-only subset and nothing the web app can do that
this cannot. Keep it that way: a command missing here is one a script has to do with `curl`
and a bearer token.

| | |
| --- | --- |
| `auth` | `login`, `logout`, `status`, `token` |
| `groups` | `list`, `show`, `create`, `rename`, `set-default-split`, `set-default-period`, `members`, `remove-member`, `balances`, `settle`, `settle-between`, `settle-up`, `activity`, `spending`, `archive`, `unarchive`, `leave`, `add-guest`, `guests`, `guest-link create\|revoke`, `link show\|create\|revoke`, and the deprecated `invite`, `invitations`, `withdraw-invitation`. `create` and `rename` accept optional `--description` context. |
| `transactions` (`tx`) | `list`, `show`, `create`, `update`, `summary`, `monthly`, `shares list\|summary`, `bank-matches`, `delete` |
| `categories` | `list`, `create`, `update`, `archive`, `unarchive`, `delete`. `create` and `update` accept optional `--description` context. |
| `merchants` | `list`, `show`, `create`, `update`, `delete` |
| `receipts` | `transcribe`, `attachments list\|upload\|download\|transcribe\|delete`, `show`, `set`, `rule`, `preview`, `divide`, `delete` |
| `split-rules` | `list`, `show`, `versions`, `versions set`, `create`, `update`, `delete` |
| `invitations` | `list`, `show`, `claim`, `decline`, `link`, `join` |
| `bank` | `list`, `link-token`, `link`, `refresh`, `sync`, `unlink` |
| `inbox` | `list`, `summary`, `matches`, `suggest`, `file`, `link`, `attachments list\|upload\|download\|transcribe\|delete`, `dismiss-match`, `ignore`, `restore` |
| `users` | `me`, `position`, `delete` |
| `settle` | `plan`, `pay`, `history` |
| `config` | `list`, `get`, `set`, `unset`, `profiles`, `path` |
| `schema`, `completion` | no subcommands; see [For agents](#for-agents) and [Shell completion](#shell-completion) |

The flags of each are in `groupsplit <command> --help`, and all of them at once in
`groupsplit schema --json` -- which is generated from the same tree that parses the
arguments, so this table can go stale and that one cannot.

`groups remove-member` and `groups settle` read the group's roster or listing before writing,
so a confirmation can name what it is about to change rather than echo a guid back.
`bank unlink`, `categories delete`, `split-rules delete` and `receipts delete` do the same.
The roster read also lets `groups remove-member` refuse before asking: somebody not on it is
an error straight away, not a confirmation the server would then refuse. `receipts divide`
reads the division itself, so the prompt lists each share, by user id, and what it comes to.
It is money moving between people's balances, and those figures are what to read before
agreeing.

`groups settle-up` reads the balances for the same reason, so its confirmation lists the
repayments it is about to write. `settle pay` reads the cross-group plan for the same reason
and one more: the payment it records can span two groups, so the confirmation names the
groups it will land in and what each one takes. Without that it would be asking somebody to
agree to bookkeeping they cannot see. `groups link create` only asks when the group already
has a link to lose; making a group's first one destroys nothing.

`merchants update` and `merchants delete` ask nothing: a merchant belongs to no group, so a
rename, a new logo or a deletion would change what every group that has spent there sees,
and the server refuses all three for every account (`MERCHANT_EDIT_FORBIDDEN`, exit code 2,
with a remediation that says so rather than pointing at `auth status`). The commands stay so
that the refusal comes back with its code. `merchants list` and `merchants show` see only the
places you already have an expense or an imported bank row at, at most 20 to a listing, and
`--search` needs at least 2 characters (`MERCHANT_SEARCH_TOO_SHORT`). `merchants create`
hands back the existing merchant when the name is already taken, and its `--logo-url` has to
be on the bank provider's logo hosts (`MERCHANT_LOGO_NOT_ALLOWED`).

`transactions delete` reads too, and its read is allowed to come back empty. It takes either
kind of transaction, but the read behind the prompt is the expense listing, which cannot see
a settlement, so for a settlement the confirmation names the id and says what deleting it
does. Only a 404 is taken that way. Any other failure of the read stops the command, because
a fault read as "just a settlement then" would delete a row nobody had been shown.

A transaction somebody who has left the group paid for or had a share of cannot be deleted at
all (`TRANSACTION_MOVES_DEPARTED_BALANCE`, naming them): they left square, and deleting it
would leave them owed or owing with nobody to settle with. The prompt says so; the refusal
comes after it, because only the API knows who has left.

## What each command does

How each command behaves (what it refuses and why, which flags contradict each other, what
its confirmation lists) is written once, in the agent skill's
[command reference](../skills/groupsplit/references/commands.md). A second copy here drifted
from it (#325), so this page keeps the contract and the setup. The flags themselves are in
`groupsplit <command> --help`.

| For | Read |
| --- | --- |
| Settling up in a group, with one person across groups, or through a day | [Settling up](../skills/groupsplit/references/commands.md#settling-up), [Settling with a person, across groups](../skills/groupsplit/references/commands.md#settling-with-a-person-across-groups) |
| Guests, their links and the receiving side | [Guests](../skills/groupsplit/references/commands.md#guests), [The two ways in to a group](../skills/groupsplit/references/commands.md#the-two-ways-in-to-a-group) |
| Whose expenses a listing covers, and what you owe | [Filtering and paging](../skills/groupsplit/references/commands.md#filtering-and-paging) |
| Your personal group, and an expense in another currency | [The personal group and other currencies](../skills/groupsplit/references/commands.md#the-personal-group-and-other-currencies) |
| Editing an expense, and what divides it | [Setting exact shares](../skills/groupsplit/references/commands.md#setting-exact-shares---split), [Changing an expense](../skills/groupsplit/references/commands.md#changing-an-expense), [One expense that is one person's](../skills/groupsplit/references/commands.md#one-expense-that-is-one-persons---split-rule) |
| Split rules, and writing a rule's past | [Dividing an expense](../skills/groupsplit/references/commands.md#dividing-an-expense-split-rules-create-and-update), [Writing a rule's past](../skills/groupsplit/references/commands.md#writing-a-rules-past-versions-set) |
| Splitting a bill by its items, and receipt files | [receipts](../skills/groupsplit/references/commands.md#receipts) |
| Linking a bank and filing its rows | [bank](../skills/groupsplit/references/commands.md#bank), and the [groupsplit-inbox skill](../skills/groupsplit-inbox/SKILL.md) for filing without recording the same money twice |

Why guests and bank sync work as they do is in [guests.md](guests.md) and
[bank-sync.md](bank-sync.md) ([duplicate detection](bank-sync.md#duplicate-detection)).

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

That version is computed, not left to MinVer, because a release tag lives on `main`'s merge
commit, which is not an ancestor of `dev`. MinVer would never see it, and every dev build
would claim `0.0.0-alpha` while main sat at `0.0.5`. Reading the tag list directly ignores
reachability and keeps the two lines in step.

Three more consequences:

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
would leave the old binary installed while reporting success: a stale tool that gives no sign
of being stale. Publishing on every merge would make that routine, so the version comes from
git and the workflow claims a new one every time the CLI changes.

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
controllers would stop triggering the day someone moves a file. The tests are left out, since
a test-only change does not alter the shipped tool, but they still run and gate the publish
whenever the workflow fires.

The filter judges each push on its own. If a CLI change's run fails or is cancelled, the
*next* push will not pick it up, because that push has no CLI change of its own to trigger
on. The release sits unpublished and nothing goes red. Re-run the workflow by hand
(`workflow_dispatch`) if that happens; a manual run ignores the filter.

### Without installing

Nothing has to be installed to run it:

```bash
dotnet run --project src/GroupSplit.Cli -- groups list
```

### Why it is not a local tool

`dotnet-tools.json`, alongside `nswag` and `dotnet-ef`, looks like the natural home: one
pinned version everyone shares. It is left out on purpose.

The manifest records a package id and a version but never where to get them, so
`dotnet tool restore` resolves against whatever sources the machine has configured. Adding
this package would make that restore fail for every contributor without a `read:packages`
token, including those who never touch the CLI, turning a working `git clone` into one that
needs credentials first. A global tool keeps that cost on the people who want the tool.

### Standalone binaries

`dotnet publish -r <rid>` works today if a tool package is the wrong shape. Native AOT does
not -- the generated client binds through reflection-based System.Text.Json and would need a
`JsonSerializerContext` first.

## Pointing it at a server

**No URL is compiled into the binary.** One build talks to a local Aspire run, a staging box
and production; which one is a runtime decision.

A deployment serves the API and Keycloak under a single public origin (the API behind the
web app's `/api` forwarder, Keycloak under `/idp`), so one setting is enough:

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
where the app is published, so `groupsplit groups link show` composes it here. Without a
server origin it can only give you the token and say why.

`/native/api` rather than `/api` because they are different doors. `/api` is the browser's:
it authenticates with the web app's session cookie and swaps in the token held inside it, so
a request carrying its own token is refused there. `/native/api` forwards to the same API and
leaves the Authorization header alone, which is what a client holding a token needs: this
CLI and the MAUI app.

Sources are consulted in this order, first one wins:

1. `--server`
2. `GROUPSPLIT_SERVER`
3. the `server` key of the active profile in `~/.config/groupsplit/config.json`

When the API and Keycloak are *not* under one origin (every local Aspire run, where each
resource gets its own port), set the two directly instead. Either replaces the
derived value, and setting both means no server origin is needed at all:

```bash
export GROUPSPLIT_API_URL=https://localhost:7043
export GROUPSPLIT_AUTHORITY=http://localhost:8080/realms/group-split
```

`GROUPSPLIT_API_URL` on its own is a complete configuration when `GROUPSPLIT_TOKEN` is also
set: nothing contacts the identity server on that path, so nothing needs its URL. Only the
commands that sign in need the authority.

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

`groupsplit config list` shows what the *next* command will talk to, after flags,
environment and file have all been applied, and which source each value came from.

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
  discovery document (issuer, token, device authorization, logout) has to sit on the same
  origin as the authority you configured, which also rules out an https authority whose
  document names an http endpoint. Nothing legitimate is turned away: Keycloak serves its
  endpoints under its own origin.
- **It will not open a verification address that is not an http or https URL.** Opening one
  goes through the desktop's handler, which will just as happily run an executable or a UNC
  path, and that address came off the wire.

Credentials are written to `~/.local/share/groupsplit/credentials.json`, keyed by realm and
client so several servers can be signed in at once. On Unix the file is narrowed to `0600`
before anything is written to it; on Windows it inherits the permissions of `%APPDATA%`, which
is already user-scoped. Expired access tokens are refreshed silently; a spent refresh token is
discarded so the next command says "sign in" rather than failing the same way twice.

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

### Deleting the account

`groupsplit users delete` deletes the account you are signed in as, and its sign-in with it:
the Keycloak login, its password, any linked Google account and every session it holds, the
web app's included. Nobody can sign in as it again. It is refused with `ACCOUNT_NOT_SETTLED`
while any group balance is not zero, and asks for confirmation like any other destructive
command. On success the CLI throws away its stored credentials, since the login they belong
to is gone.

A token issued before the deletion stays valid until it expires, a few minutes at most, and
the API refuses it with `ACCOUNT_DELETED` (exit code 2) rather than treating it as a new
sign-in. Run `groupsplit auth logout` if a stored session still answers that way. What
deletion keeps and removes, and why, is in
[Deleting an account](keycloak.md#deleting-an-account).

## Amounts

Every amount on the command line is written one way, on every machine: digits, a `.` for the
decimal point, no thousands separators, at most two decimal places. That covers the
arguments (`transactions create <name> <amount>`, `groups settle ... <amount>`), the options
(`--amount`, `--subtotal`, `--tax`, `--tip`, `--total`), the `--split` and `--percent` pairs,
and the numbers inside a receipt `--item`, where a quantity may have three places.

| Typed | Read as |
| --- | --- |
| `12.50`, `12.5`, `12` | 12.50 |
| `12,50` | Refused, exit code 3 |
| `1,234.56` | Refused, exit code 3 |
| `1.234,56` | Refused, exit code 3 |
| `12.555` | Refused, exit code 3 -- not rounded |

A refusal comes before any request, says which value it could not read, and says what to
type instead:

```text
Could not read amount '12,50'. Use '.' for the decimal point and no thousands separators, as in 1234.56.
```

The format does not follow the machine's language settings. The parser used to: on an
en-US machine `12,50` was read as 1250, and on an es-ES one `12.50` was. Shares misread the
same way still added up to the misread total, so a $12.50 dinner could be recorded as a
$1,250 one with nothing refusing it (#313). A script now means the same amount wherever it
runs.

The web app reads either a `.` or a `,` as the decimal point, because a phone keypad in many
languages offers only the comma; it refuses grouping and a third decimal place the same way.

## Output

Two renderings of the same data, chosen automatically:

| stdout is | Default |
| --- | --- |
| a terminal | text -- tables, colour, sized to the window |
| anything else | JSON |

So a person gets a readable table and `groupsplit groups list \| jq` gets JSON, without either
having to ask. `--json` (or `--output json`) and `--output text` force the choice.

Underneath, everything else here depends on two rules:

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

It narrows results, and the [confirmation envelope](#confirming-changes) is not one: an exit 4
always carries all of it, whatever `--fields` named.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success. stdout is trustworthy. |
| `1` | A general failure. |
| `2` | Authentication: not signed in, expired, or refused. |
| `3` | The invocation was wrong: bad usage, a rejected value, or no server configured. |
| `4` | A mutation needs confirming. stdout carries the confirmation envelope. |
| `5` | Rate limited: too many requests of one kind (receipt transcription, bank link and sync, invitations) in the window. Nothing was done. The envelope's `retryAfter` says how many seconds to wait; the same command then succeeds. |

Rely on the difference between 1 and 3: **3 means retrying will not help until something
changes.**

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
API already names. Codes the CLI raises itself are prefixed `CLI_`. Most of them mean the
request was never sent. These three are about whether it was:

| Code | Meaning |
| --- | --- |
| `CLI_SERVER_UNREACHABLE` | The name did not resolve, the connection was refused, or TLS failed. Nothing was sent. |
| `CLI_OUTCOME_UNKNOWN` | The request may have reached the server and no answer came back: the connection broke after it was sent, or the server took longer than the client waits. **It may have been carried out.** A read is safe to repeat; before repeating a change, look for it, or it may be recorded twice. |
| `CLI_CANCELLED` | Interrupted (Ctrl+C), or a confirmation declined. An interrupt during a change may not have stopped it. |

The full list of `CLI_` codes is in the agent skill's
[protocol reference](../skills/groupsplit/references/protocol.md#codes-the-cli-raises-itself).

The exit code follows the HTTP status (a 409 is `3`), with one exception:
`IDENTITY_EMAIL_IN_USE` is a 409 that exits `2`. The server refuses the sign-in itself, on
every command, because its email already belongs to another account -- somebody deleted and
recreated in Keycloak. Signing in again as the same person brings back the same refusal; the
remediation says to contact support with the `traceId`, and support resolves it as
[Sign-ins and accounts](keycloak.md#sign-ins-and-accounts) describes.

`traceId` is the API's own, and matches its log line.

`retryAfter` is present only on a `RATE_LIMITED` refusal (exit code 5): the whole seconds until
the identical command will be let through. The limits are per signed-in person and cover receipt
transcription, bank link-token, linking, refresh and sync, and invitations and join links; see
[errors.md](errors.md#too-many-requests-429).

## Confirming changes

Destructive commands stop unless confirmed.

At a terminal you get a prompt. Everywhere else (a pipe, CI, an agent) you get exit code 4
and a description of what would have happened on stdout:

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
does not have to reconstruct the invocation: it re-runs that, or shows it to a human first.
`--yes` skips the gate.

It is read back out of the parsed invocation rather than written per command, so it carries
every option that was given, `--profile` and `--server` included so it acts on the same
account, and adds only `--yes`. On the way, a file is named by its absolute path, each word is
quoted for a POSIX shell when it needs to be, and a value beginning with `-` or `@` is joined
to its option with `=`. A figure the command worked out rather than was given is stated:
`settle pay` without `--amount` comes back with `--amount` set to what the prompt named.
`ConfirmCommandTests` runs every gated command both ways (with `--yes`, and by re-running
its `confirmCommand` from another directory) and requires the same writes from each.

The server can still change between the prompt and the confirmation, and the confirmed run
acts on what is there then: `settle-up` and `receipts divide` work their figures out again.

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

1. **`groupsplit schema --json`** prints the whole command tree (every command, flag, type,
   arity and the exit-code table) in one call. It is generated from the same tree that parses
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

### The skills

The four points above are the whole contract, but an agent has to be told them before its
first command rather than after its first mistake. `skills/` carries that as two agent
skills, `groupsplit` (the protocol) and `groupsplit-inbox` (the bank-row reconciliation loop),
installable into any agent:

```bash
npx skills add Sussman-Club/group-split
```

or, for Claude Code as a plugin:

```bash
claude plugin marketplace add Sussman-Club/group-split
claude plugin install groupsplit@group-split
```

They live in this repository, beside the CLI, so that changing the CLI and changing what an
agent is told about it is one commit rather than two. They describe the protocol and not the
flag list, because `schema` already prints the flag list and cannot be wrong about it. See
[skills/README.md](../skills/README.md).

## Running it against the local stack

The AppHost registers the CLI as an explicit-start resource, so `WithReference` hands it this
run's API and realm URLs and its requests appear in the dashboard's traces beside the API
spans that served them. Start it from the dashboard when you want that.

For working on it, run it directly and let the environment point it at the stack:

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
and carrying the same `api` audience mapper as `web-app`; without that mapper the API
rejects its tokens for the wrong audience.

Keycloak imports a realm on first start only, and both the local Keycloak (`WithDataVolume`)
and the deployed one keep theirs, so a realm imported before the CLI existed has no `cli`
client. The `realm-settings` worker takes care of that: on every start, locally and on every
deploy, it creates the client from this file when it is missing, and otherwise puts back its
grants (device grant on, every other grant off) and its `api` audience mapper. Nothing in the
client depends on the environment, so creating it writes exactly what an import would have.
See [Applying this to a realm that already exists](keycloak.md#applying-this-to-a-realm-that-already-exists).

`GROUPSPLIT_TOKEN` needs no realm change at all.
