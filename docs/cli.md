# The command line

`groupsplit` is the terminal client for the API. It is built from the same OpenAPI document
as the Blazor and MAUI clients, so its request and response types are the ones in
`GroupSplit.Shared` and it cannot drift from the API's contract.

It is designed to be driven by two kinds of caller at once: a person at a terminal, and a
program -- a script, a CI job, or an AI agent shelling out. Everything below that looks like
a nicety for the second kind is load-bearing.

- [What it covers](#what-it-covers)
- [Settling across groups](#settling-across-groups)
- [Installing it](#installing-it)
- [Keeping it updated](#keeping-it-updated)
- [Pointing it at a server](#pointing-it-at-a-server)
- [Signing in](#signing-in)
- [Bank sync](#bank-sync)
- [An expense in another currency](#an-expense-in-another-currency)
- [Editing an expense](#editing-an-expense)
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
this cannot, which is the property worth keeping: a command missing here is a thing a script
has to reach for `curl` and a bearer token to do.

| | |
| --- | --- |
| `auth` | `login`, `logout`, `status`, `token` |
| `groups` | `list`, `show`, `create`, `rename`, `members`, `remove-member`, `balances`, `settle`, `settle-up`, `activity`, `archive`, `unarchive`, `leave`, `add-guest`, `guests`, `guest-link create\|revoke`, `link show\|create\|revoke`, and the deprecated `invite`, `invitations`, `withdraw-invitation` |
| `transactions` (`tx`) | `list`, `show`, `create`, `update`, `summary`, `monthly`, `shares list\|summary`, `bank-matches`, `delete` |
| `categories` | `list`, `create`, `update`, `archive`, `unarchive`, `delete` |
| `merchants` | `list`, `show`, `create`, `update`, `delete` |
| `receipts` | `transcribe`, `attachments`, `show`, `set`, `rule`, `preview`, `divide`, `delete` |
| `split-rules` | `list`, `show`, `versions`, `versions set`, `create`, `update`, `delete` |
| `invitations` | `list`, `show`, `claim`, `decline`, `link`, `join` |
| `bank` | `list`, `link-token`, `link`, `refresh`, `sync`, `unlink` |
| `inbox` | `list`, `summary`, `matches`, `file`, `link`, `attachments`, `dismiss-match`, `ignore`, `restore` |
| `users` | `me`, `position`, `delete` |
| `settle` | `plan`, `pay`, `history` |
| `config` | `list`, `get`, `set`, `unset`, `profiles`, `path` |

The flags of each are in `groupsplit <command> --help`, and all of them at once in
`groupsplit schema --json` -- which is generated from the same tree that parses the
arguments, so this table can go stale and that one cannot.

Two commands read the group's roster or listing before writing, so a confirmation can name
what it is about to change rather than echo a guid back: `groups remove-member`,
`groups settle`. `bank unlink`, `categories delete`, `split-rules delete`
and `receipts delete` do the same. The roster read also lets
`groups remove-member` refuse before asking: somebody not on it is an error
straight away rather than a confirmation the server would then refuse. `receipts divide` goes further and
reads the division itself, so the prompt lists what each person ends up owing rather than
saying the shares will change -- it is money moving between people's balances, and the
figures are the thing worth reading before agreeing to them.
`merchants update` and `merchants delete` ask nothing: a merchant belongs to no group, so a
rename, a new logo or a deletion would change what every group that has spent there sees,
and the server refuses all three for every account (`MERCHANT_EDIT_FORBIDDEN`, exit code 2,
with a remediation that says so rather than pointing at `auth status`). The commands stay so
that the refusal comes back with its code. `merchants list` and `merchants show` see only the
places you already have an expense or an imported bank row at, at most 20 to a listing, and
`--search` needs at least 2 characters (`MERCHANT_SEARCH_TOO_SHORT`). `merchants create`
hands back the existing merchant when the name is already taken, and its `--logo-url` has to
be on the bank provider's logo hosts (`MERCHANT_LOGO_NOT_ALLOWED`).
`groups settle-up` reads the balances for the same reason, so its confirmation lists the
repayments it is about to write rather than describing them. `settle pay` reads the
cross-group plan for the same reason and one more: the payment it records can span two
groups, so the confirmation names the groups it will land in and what each one takes.
Without that it would be asking somebody to agree to bookkeeping they cannot see.
`groups link create` reads for a further reason: it only asks when the group already has a
link to lose, and making a group's first one destroys nothing.

`transactions delete` reads too, and is the one that is allowed to come back empty-handed.
It takes either kind of transaction, but the read behind the prompt is the expense listing,
which cannot see a settlement -- so for one of those the confirmation names the id rather
than the row, and says what deleting it does instead. Only a 404 is taken that way: any
other failure of the read stops the command, because a fault that read as "just a
settlement then" would delete a row nobody had been shown. A transaction somebody who has
left the group paid for or had a share of cannot be deleted at all
(`TRANSACTION_MOVES_DEPARTED_BALANCE`, naming them): they left square, and deleting it would
leave them owed or owing with nobody to settle with. The prompt says so; the refusal comes
after it, because only the API knows who has left.

## Settling across groups

`groups settle` and `groups settle-up` work inside a group, because a balance belongs to a
group. `settle` works on a person, because a payment does -- and somebody who owes the same
friend in two groups pays them once.

```bash
groupsplit settle plan --json
```

One line per person, largest first, each naming the groups its figure comes from. The three
figures over the top are the same ones `users position` leads with, and for the same reason
the two gross sides are kept apart there: they are different people and they do not cancel
out.

```bash
groupsplit settle pay <user-id> --amount 70 --direction YouPaidThem --dry-run
```

`--dry-run` prints the per-group breakdown and stops; without it the command goes through
the usual confirmation gate. `--amount` defaults to the whole of what is outstanding between
you, and the direction is stated rather than read off the balance -- a debtor recording "I
paid you back" is acting precisely while the balance still says they owe.

Behind one payment the API writes one transfer per group, in a single save, spending the
amount over the groups largest first. That last part is not a flag: which group a payment
lands in is bookkeeping, and the person handing over the money should not have to do it.

```bash
groupsplit settle history --json
```

Every repayment you were party to, in any group, newest first. It answers "did I already pay
this?", which used to mean opening each group's activity in turn.

### Settling through a day

Groups that square up month by month want "clear August" while September stays open.
`--through <day>` does that on every command that reads or settles what is owed:

```bash
groupsplit settle plan --through 2026-08-31 --json
groupsplit settle pay <user-id> --through 2026-08-31 --dry-run
groupsplit groups balances <group-id> --through 2026-08-31
groupsplit groups settle-up <group-id> --through 2026-08-31 --dry-run
```

Through a day, what is owed counts the expenses dated up to the end of it and the
repayments made for it -- covering through that day or an earlier one, or claiming no period
and dated by then. **Repayments made for later count only as far as they pay down what the
period left.** Money paid goes against the oldest debt first, so somebody who paid August's
share in mid-September is not asked for it again when August is settled in October, and
settling the same period twice finds nothing the second time; but paying a September expense
in September never turns into being owed it back for August. Nothing else after the day
moves the figure: being ahead in October does not shrink what September left. Once a later
period has been settled, though, a period never asks for more than that one leaves owing --
settling September can route August's debts through somebody else. The day ends
at midnight on this machine's calendar: the CLI sends its current UTC offset beside the day,
as the web app does.

The repayments it records remember the day. `settle history` shows it on the row, and
`transactions show <settlement-id>` says what period the settlement covered and whether that
period is still square -- an expense dated inside it and added afterwards shows up there as
still owed. `groups settle` takes `--through` too, but only to record it: the amount there is
always the one you give.

```bash
groupsplit transactions update <settlement-id> --covers-through 2026-08-31
groupsplit transactions update <settlement-id> --no-covers-through
```

A settlement's day can be corrected or cleared afterwards. An expense has no period, and
naming one on it is refused before anything is sent.

## Guests

A group names whoever is sharing the costs. Somebody with no account in it is a **guest**:
the members record everything for them -- their expenses, their shares, what they pay back
and are paid -- and nobody has to sign up for any of it.

```bash
groupsplit groups add-guest <group-id> Carlos Nuria
```

```
 User id                               Name    Added       Link
 f1b00001-0000-4000-8000-000000000001  Carlos  2026-10-05  none
 f1b00002-0000-4000-8000-000000000002  Nuria   2026-10-05  none
```

**One id per guest**, the user id, and it is the id for everything: a split, a payer, a
repayment, a link, removing them. It is the same id `groups members` lists them under:

```bash
groupsplit groups members <group-id>
```

```
 Id                                    Name              Email             Status
 0d1ac8ae-709c-4c4c-8f0f-0c7e951b3a52  Anabel Benítez    anabel@test.com   member
 f1b00001-0000-4000-8000-000000000001  Carlos            -                 guest
```

```bash
groupsplit tx create --group <group-id> --amount 90 --name Rent \
    --split <anabel-id>=60 --split <carlos-id>=30

groupsplit tx create --group <group-id> --amount 40 --name Taxi --paid-by <carlos-id>
```

Their balance is in `groups balances`, marked `(guest)`, and in the transfers underneath it
like anybody else's. Settling with one is an ordinary repayment, recorded by a member:

```bash
groupsplit groups settle <group-id> <carlos-id> 30            # Carlos paid you 30
groupsplit groups settle-between <group-id> <carlos-id> <nuria-id> 15   # Carlos paid Nuria
groupsplit settle pay <carlos-id>                             # across groups; marked (guest)
```

### Removing a guest

```bash
groupsplit groups remove-member <group-id> <carlos-id> --yes
```

The same command as for a member, on the same terms: refused with `GROUP_MEMBER_NOT_SETTLED`
(and their balance) until they are square, which for a guest means a member recording what
they paid or were paid back. Once removed they are a past guest: still in the history and in
`past-members`, their shares on old expenses fixed (`TRANSACTION_MOVES_DEPARTED_BALANCE`), and
nobody can name them for new money. Removing a guest leaves the group's join link alone --
they never had a way to read it.

### Their link

A guest can be given a link, so the real person can take their place: whoever claims it
joins the group and everything recorded for that guest becomes theirs.

```bash
groupsplit groups guest-link create <group-id> <carlos-id>        # issue, or show the one out
groupsplit groups guest-link create <group-id> <carlos-id> --new  # replace it; asks first
groupsplit groups guest-link revoke <group-id> <carlos-id>        # stop it; the guest stays
groupsplit groups add-guest <group-id> Carlos --link              # add and issue in one go
```

`create` without `--new` is idempotent: asking again prints the link already out, so it is
safe to run whenever somebody needs it re-sent.

A guest's link **expires 30 days after it was issued**. `groups guests` says until when each
link works, and marks one that ran out unclaimed as expired (in JSON: `linkExpired` true,
`hasLink` false, no `token`, and `linkExpiresAt` the moment it lapsed). `create` on an expired
link issues a new one, with or without `--new`, and does not ask first -- nothing that works
is lost. The new link counts as issued now, for its own expiry and for the rejoin rule. The link is `<web-app>/claim/<token>`; the
text output prints that URL when the CLI knows the server origin, and the JSON carries the
token. Nobody is notified: sending the link is the group's job. Replacing a link kills the
old one *and* forgets who had opened it, so a link that leaked is not handed on.

### The receiving side

```bash
groupsplit invitations list            # links you have opened that still work
groupsplit invitations show <link>     # what it leads to, claiming nothing
groupsplit invitations claim <link>    # join as that guest, taking on their position
groupsplit invitations decline <link>  # "this isn't me": the link stops working
```

`list` is every guest link this account has **opened** and that still works, which the API
writes down as it happens, and it carries the tokens and when each expires. That makes it the way back to a link
when the message it arrived in is gone. The same list is the "Waiting on you" row on the web
app's home page.

All four take the whole URL as readily as the token inside it. `show` says the group, its
size, who sent the link and which guest it was made out for -- and deliberately nothing about
the money: a link can be forwarded, and what is recorded for that guest is the group's until
somebody claims it.

`claim` is gated by the [confirmation protocol](#confirming-changes), because it takes on a
position rather than only joining a group: the shares recorded for that guest, the expenses
they were down as having paid for and their places in the rules become the claimer's. No
amount changes, and the claimer's balance does. If the claimer is already a member and had
recorded repayments with that guest -- the same person, twice -- those repayments are
deleted, because they would read as paying yourself; the answer counts them. Afterwards the
link is spent, so a forwarded copy gets the same 404 a mistyped URL does -- and so does a
link that has expired.

`decline` only stops the link. The group keeps the guest and everything recorded for them,
and a member can send a new link. Nothing moves, so there is no money to report:

```
 Group    The flat
 Guest    Carlos
 Outcome  link turned down

Carlos is still a guest of The flat. Nothing moved. A member can send a new link.
```

Do not treat a guest's link like a group join link. `groups link create` makes the group's
open door: reusable, expiring, and it lets somebody in as *themselves*, claiming nothing.
Forwarding one of those costs a group an unwanted member; forwarding a guest's link hands
somebody else's debts to a stranger.

### From before guests

`groups invite`, `groups invitations` and `groups withdraw-invitation` still work, for one
release, against the deprecated routes: `invite` is `add-guest --link`, `invitations` lists the
guests that have a link out (by their old invitation id), and `withdraw-invitation` now only
stops the link -- the guest stays. The JSON still carries `isPendingInvitee` beside `isGuest`
for the same release.

The whole design, and why, is in [guests.md](guests.md).

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
showing `login required` needs; `groupsplit bank list` says which ones do. The state covers
everything the bank needs the person for, not only a changed password: a locked account, a
password it wants reset, an agreement waiting on its own site, no account shared. Whatever
it is, the person deals with it at the bank and then signs in again here.

Update mode is also how somebody shares an account they did not share when they linked. It
mints no new item and hands back no usable public token, so nothing on the linking path
runs and this application would never hear about the account. `bank refresh` is what tells
it:

```bash
groupsplit bank refresh <connection-id>    # re-read the accounts, then queue a sync
```

A connection listed as `account not shared` is one the bank has an account for that is not
being imported -- either a sync met rows on it, or the provider said so. The way out is an
update-mode sign-in with that account ticked, then `bank refresh`. `sign-in expiring` means
the connection still works but will stop soon unless somebody signs in again, and an account
whose `Access` column reads `withdrawn` has been revoked at the bank on its own; the rest of
the connection carries on.

Everything after linking is ordinary:

```bash
groupsplit bank sync <connection-id>       # queued, not awaited: a full history takes a while
groupsplit inbox list                      # what arrived
groupsplit inbox file <row-id> --group <group-id> --category-id <category-id>
groupsplit inbox ignore <row-id>           # undone by: inbox restore
```

Filing is undone by deleting the expense: `groupsplit transactions delete <transaction-id>`
puts the row back in the inbox as waiting, with the receipt files it had there, and its
confirmation says so. A row the bank has withdrawn since goes with the expense instead.

`bank unlink` deletes the receipt files still waiting on that bank's inbox rows, stored
objects included; files already on an expense stay with it. It succeeds when the provider no
longer knows the item -- removed at its end, or by an earlier unlink that failed after
telling it.

`bank sync` answers as soon as the pull is queued, because nothing waits on a bank inside a
request. `groupsplit inbox summary` is the cheap way to poll for it -- it is one count, not a
page of rows.

Filing copies what the bank said. The date, the amount and the currency have no flags on
`inbox file` for that reason: correcting the bank is an edit to the expense afterwards.
Keeping a row personal -- `inbox file` with no `--group` -- keeps the bank's currency too,
so a 20.00 EUR charge is a 20.00 EUR expense; filing it into a group in another currency is
refused. See [an expense in another currency](#an-expense-in-another-currency).

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

Nothing has to wait for the refusal. `inbox list` marks the rows and counts them underneath,
and `groupsplit inbox matches <row-id>` names what each row matched and why -- how many days
apart and how far the amounts are -- without filing anything.

#### How sure the suggestion is

A match comes back with a `confidence`, and it is worth reading before deciding how much of
somebody's attention to spend on it. Measured against real spending, the amount-and-date test
on its own lands on unrelated money 11.5% of the time; amounts that agree **to the cent** do
so 0.18% of the time. Those are two different claims and they are marked as two:

| `confidence` | What it means | `inbox list` | `inbox matches` |
| --- | --- | --- | --- |
| `Confident` | The amounts agree to the cent | `(duplicate?)` | `(same amount)` |
| `Possible` | They differ by a tip, or a figure somebody rounded | `(similar)` | `(similar amount)` |

**Filing is refused over both**, so both are worth answering. A tip added to a dinner is the
likeliest duplicate there is and it is only ever `Possible` -- treating that as noise is how
the same money ends up in the ledger twice. What the grade tells you is how confidently to
put it to somebody: a confident match is worth interrupting them with, and a possible one is
worth mentioning while they are already deciding.

`inbox summary` counts the confident ones only, so a number read as "some of these are
already recorded" is right about it. `inbox list` says how many merely look similar.

The other order is asked from the other end. Somebody who typed the expense first wants to
know when the card charge arrives, and the row is what they need the id of:

```bash
groupsplit transactions bank-matches <transaction-id>
```

Both directions answer with the same two commands. Which of the three applies is a question
about the money, so no default is picked: `--file-anyway` is never on unless it is asked for,
because the refusal is the whole mechanism that stops the second expense coming into being
before somebody has been told about the first.

## Whose expenses a listing covers

`transactions list` and `transactions summary` are about **you**: the expenses you paid
for, across every group and outside them. That is deliberate, and for a long time nothing
said so.

```bash
groupsplit tx list                               # expenses you paid for
groupsplit tx summary                            # and their total
```

`--group` switches them to the group's own ledger -- every expense in it, whoever paid:

```bash
groupsplit tx list --group <group-id>            # the group's whole ledger
groupsplit tx summary --group <group-id>         # and its total
```

It used to be a filter applied to your listing, which narrowed to the payer as well. So
`--group` answered "what have I paid in this group" under a flag documented as a group
filter, and an empty answer read as an empty group. It cost real time twice: during the
spreadsheet migration a group holding 1,411 expenses reported a count of 2, which looked
exactly like a failed import; and in September 2026 a question about a group's monthly
spending was answered "nothing has been spent" when the group had spent $39,429.42.

Both commands moved together, because the listing and the total narrowed independently: a
page of the group's rows under a figure counting only yours is worse than either being
wrong alone, since the two agreeing is what a reader checks.

To ask for one person's spending, say so:

```bash
groupsplit tx list --group <group-id> --paid-by <user-id>
```

That is what `--group` was doing by accident. Ids come from
`groupsplit groups members <group-id>`.

Three things are worth knowing about the answers:

- **A group you are not in is refused**, exit code 3, rather than answered with an empty
  list. The API returns an empty page to a non-member on purpose, so the CLI checks and
  says which of the two it was. This includes a group you have **left**: your own expenses
  in it are still in `transactions list`, but the group's ledger is the group's.
- **Settlements are absent** from both, and from `shares` and `monthly`. A transfer is a
  different type, so none of these figures has been paid back. Everything a group did,
  transfers included, is `groupsplit groups activity <group-id>`; where you stand is
  `groupsplit users position`.
- **`monthly` and the two `shares` commands stay yours.** Their columns are what you paid
  and what your share came to, and neither has a group-wide reading, so `--group` there
  narrows rather than switches. The flag's own help text says which it does on each
  command.

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

## An expense in another currency

A group keeps its balances in one currency, and nothing in GroupSplit converts between
currencies. Your own ledger is different: nothing adds a personal expense to anybody
else's money, so it may be in any currency.

```bash
groupsplit tx create "Dinner in Lisbon" 20 --currency EUR     # personal, in euros
groupsplit inbox file <row-id>                                # kept personal, in the row's currency
```

`--currency` takes any three-letter ISO 4217 code. Leave it off in a group: the expense takes
the group's own, and any other is refused. Once recorded, an expense keeps its currency --
there is no flag that changes it, because changing it would relabel the amount rather than
convert it.

`CURRENCY_MISMATCH`, exit code 3, is the answer wherever money would cross into a group in
another currency: `tx create --group` with a different `--currency`, `inbox file --group`
for a row in another currency, and `tx update --group` moving an expense into one -- or
between two groups in different currencies. Nothing is written. The way out is to keep it
personal, or to put it in a group that uses that currency.

In text output an amount names its currency only when it is not the default, so a column of
dollars stays a column of numbers and the one row in euros reads `20.00 EUR`; `tx show`
always names it. JSON always carries `currency`.

Totals are one currency each. `tx summary` and `tx shares summary` total the matches in
`currency` -- the default unless nothing that matched is in it -- and list every other one on
a line of its own, never added in:

```bash
groupsplit tx summary
# 3 expenses totalling 25.00
# Not in that total: 1 in EUR totalling 20.00 EUR
```

With `--json` those are `otherCurrencies`. `tx monthly` is a series, so it is drawn in one
currency and leaves the others out; it says which under the table.

## Splitting a bill by its items

For the dinner where nobody ate the same thing. An even split is wrong for it, a percentage
rule is a guess at it, and typing five amounts by hand means adding the column up yourself
and then working out everybody's share of the tax and the tip -- which is the part people get
wrong.

Every line divides by a **saved split rule version**, the same kind of rule a whole expense
divides by. "Who had what" is therefore written the way the group already writes divisions:
a rule naming Ana, a rule naming Ana and Omar, a payer rule for the thing the payer is
standing. `groupsplit split-rules list --group <id>` prints the rules and their current
version ids, which is what a line names after its `@`.

Transcribe the bill first. The subtotal, the tax and the total default to the lines added up
and the extras added on, so on an ordinary receipt you type the lines and the tip:

```bash
groupsplit receipts set 7c1e... --tip 6.00 \
  --item "Steak=22.00/tax1.80@3f25c1a8-...-444455556666" \
  --item "Risotto=16.50/tax1.35@9ab77d10-...-111122223333" \
  --item "Wine=18.00/tax1.05@5c0ffee0-...-777788889999"
```

A line reads `[<id>#]<name>=<line-total>[x<qty>][/tax<amount>][@<rule-version-id>]`. The number
before `x` is the **total printed for the whole line**, not the unit price. For example,
`Beers=27.00x3` means three beers whose line total is 27.00 (9.00 each). After the `@` comes
**one** split rule version id, and a plate the table shared is a line naming a rule
that divides between them -- the bottle above points at a rule naming Ana and Omar. Nothing
about a line says "these two people" directly: the rule says it, the line points at the rule,
and the rule is the thing the group can edit and reuse.

A line with no `@` has no rule yet, which is the ordinary state of a bill somebody is still
working through. `receipts rule` sets one line's rule afterwards, and naming no version
clears it:

```bash
groupsplit receipts rule 7c1e... <line-id> --rule-version 3f25c1a8-...-444455556666
groupsplit receipts rule 7c1e... <line-id>          # back to no rule
```

An itemized rule cannot be a line's rule -- a bill inside a line of a bill divides nothing --
and neither can a rule belonging to another group. Both are refused with `RECEIPT_INVALID`.

### Correcting a bill

`receipts set` saves the bill **whole**, so a line the command does not mention is a line that
has gone -- and its rule goes with it. For an existing line named by id, omitted source text is
preserved by the API. To fix one total without losing which rule or raw receipt description
each stored line carries, name the stored lines by their ids, which `receipts show` prints:

```bash
groupsplit receipts show 7c1e...
groupsplit receipts set 7c1e... \
  --item "a1b2c3d4-...#Steak=24.00@3f25c1a8-...-444455556666" \
  --item "b2c3d4e5-...#Risotto=16.50@9ab77d10-...-111122223333"
```

Only text before a `#` that parses as an id is read as one, so a line called `Table #4` is
still a line called `Table #4`. A line with no id is a new line, which is what writing a bill
out for the first time looks like. An id that names a line of some other bill, or the same
line twice, is refused rather than guessed at.

What the command has no flag for is **kept**. `receipts set` reads the saved bill first and
sends back the merchant and purchase date it was read with, and the tip unless `--tip` says
otherwise. A line named by its id keeps its tax amount, rate and taxable flag unless the line
states `/tax` or `/notax`, and keeps the normalized reading of its name while the name is
unchanged. So correcting one price on a taxed supermarket bill is the price and nothing else;
it used to drop the merchant, the date and every line's rate, and the bill then refused to
add up until every line was given its tax again.

`/tax8.05` says 8.05 of the bill's tax was charged on that line. An amount rather than a
rate, because an amount is what the till prints -- and rather than a flag, which is what this
used to be: one tax total was then weighed over the flagged lines by price, which is exact
only where every taxed line carries the same rate. A supermarket receipt mixing 6% food with
23% household goods divided wrongly by several euros, with the total still adding up and no
screen able to say so.

A line carries no tax unless it says so, which is what a restaurant bill under VAT wants: the
price includes it and the bill charges none on top. `/notax` still reads, and says the same
thing as saying nothing -- worth typing on a warehouse bill, where the exempt lines are the
ones you are deliberately marking. The amounts have to come to `--tax`, which the server
checks. Either side of the `@` reads the same: `Bread=4.00/tax0.92@<version>` and
`Bread=4.00@<version>/tax0.92` are one line.

For a new bill, or one on an expense whose stored shares were not produced by the bill,
`preview` says what the division would be and `divide` is what stores it. If the bill already
drives the expense's stored ledger, a valid `set` or `rule` correction automatically
recalculates those shares. If a correction makes the bill incomplete -- a line split in two,
the new half with no rule yet -- the bill remains editable and the last valid shares remain in
place until the bill is valid again, and whichever call completes it, `set` or `rule`,
recalculates them:

```bash
groupsplit receipts preview 7c1e...
groupsplit receipts divide 7c1e...
```

### Receipt files and transcription

The CLI keeps the source file and the itemised bill as two separate things. A file can be
read into a draft without saving anything:

```bash
groupsplit receipts transcribe ./receipt.pdf
```

The response is a suggestion for an expense you are about to create. Review it, create the
expense, then save the edited bill with `receipts set`; transcription never creates an
expense or divides one.

For an existing expense, upload the source file first and read it in a separate step:

```bash
groupsplit receipts attachments upload <transaction-id> ./receipt.pdf
groupsplit receipts attachments list <transaction-id>
groupsplit receipts attachments transcribe <transaction-id> <attachment-id>
groupsplit receipts set <transaction-id> --item "..."
groupsplit receipts attachments download <transaction-id> <attachment-id> ./copy.pdf
groupsplit receipts attachments delete <transaction-id> <attachment-id>
```

`attachments delete` is confirmation-gated. The supported file types are JPG, PNG, WebP
and PDF, up to 10 MB. The transcription result is still a draft: it does not save the
itemised receipt or change the expense's shares. For a bill that is already the source of
the stored ledger, a subsequent valid `receipts set` or `receipts rule` correction keeps
the shares synchronized; otherwise `receipts divide` remains the explicit step that changes
the ledger.

An imported bank row can hold the source file before it becomes an expense. Use the same
two-step upload/transcribe flow under `inbox attachments`:

```bash
groupsplit inbox attachments upload <row-id> ./receipt.pdf
groupsplit inbox attachments list <row-id>
groupsplit inbox attachments transcribe <row-id> <attachment-id>
groupsplit inbox attachments download <row-id> <attachment-id> ./copy.pdf
groupsplit inbox attachments delete <row-id> <attachment-id>
```

When the row is filed or linked, its pending receipt files move with it to the expense.
The row still has to be filed or linked explicitly; attaching or transcribing a file never
chooses a group or records an expense on the user's behalf.

### How the tax and the tip are shared out

Line by line: each line's tax is that line's, and the tip is spread over the lines in
proportion to their prices, so every line is divided by its own rule as one amount --
price, plus its tax, plus its share of the tip. Somebody holding a quarter of the food owes
a quarter of the tip, and only the tax that was actually charged on what they had.

It falls out of one calculation rather than two, so there is a single rounding and a single
remainder -- truncated to the cent, with the leftover going to the largest line, exactly as
in every other division GroupSplit does. `preview` shows both halves per person, the items
and the total, so the apportioning can be checked rather than taken on trust.

### What it refuses

A bill that does not describe the money it claims to, before dividing anything rather than
after:

- lines that do not come to the subtotal, per-line tax that does not come to the bill's tax,
  or a subtotal, tax and tip that do not come to the total (`RECEIPT_DOES_NOT_ADD_UP`)
- a total that is not the expense's amount (the same code -- the bill has to be the expense's
  own money)
- any line with no rule on it (`RECEIPT_ITEMS_MISSING_RULE`, carrying the line ids)

The last one is refused rather than spread over everybody on purpose: quietly charging five
people for one person's steak is the kind of wrong nobody checks for afterwards. A bill in
that state is still saved and still readable -- `receipts show` says how many lines are
waiting for a rule -- it simply will not divide.

A personal expense has nobody to divide lines between, so it takes no bill at all
(`SPLIT_ON_A_PERSONAL_EXPENSE`), and somebody who has left the group can still read the bill
on an expense they paid for but no longer change it (`TRANSACTION_GROUP_LEFT`).

### Making it a group's default

Every group is given one "divide it by the bill" rule and cannot write a second: the rule
holds no settings for a second to differ by, so two of them would be one division under two
names. It is in the listing like any other, and a category points at it the way it points at
any other division:

```bash
groupsplit split-rules list --group <id>          # the given one is named "Divide by the bill"
groupsplit categories update <category-id> --split-rule <rule-id>
```

There is no `--itemized` on `split-rules create`; asking for one is refused with
`SPLIT_RULE_BILL_IS_PROVISIONED`, and the refusal names the rule the group already holds.

An expense filed under that category with a receipt on it divides by the receipt, and records
which version of the rule did it. One with no receipt is refused by name -- `RECEIPT_NOT_FOUND`
-- rather than quietly falling back to an even split, which is the thing somebody itemising a
dinner was trying to avoid.

An expense under any other rule can still be divided by its bill: `receipts divide` stores
the shares the lines come to as stated amounts, the same as typing them, and the expense goes
on being an expense that divides by its own rule.


## Editing an expense

`transactions update` sends a JSON Patch of **only** the flags you passed, and that is
load-bearing rather than an optimisation. The API reads the patch as well as applying it:
saying nothing about the shares keeps a division somebody typed. Naming them with `--split`
means those exact amounts, checked against the total.

A division **nobody typed** is treated differently, and the difference is worth knowing.
Shares a rule worked out -- or an even division under no rule, which the app worked out just
as surely -- follow the things they were worked out from: change the amount, the payer, the
category or the group and they are worked out again at the new values. Change only the name,
the note, the date or the merchant and nothing moves, whatever divided it. So the rule is not
"silence keeps the shares" but "silence never restates a decision somebody made".

```bash
groupsplit tx update <id> --name "New description"            # existing shares are preserved
groupsplit tx update <id> --split <user-id>=30.00 --split <user-id>=16.00
groupsplit tx update <id> --personal                          # take it out of its group
groupsplit tx update <id> --description ""                    # clear the note
```

So a command that names nothing is refused (exit code 3) rather than sent as an empty patch,
which the server would accept as a successful no-op.

`--group` and `--personal` contradict each other, as do `--category-id` and `--no-category`
and `--merchant-id` and `--no-merchant`; any such pair is refused before anything is sent. So
is `--split-rule` alongside either `--split` or `--redivide`, which are three answers to one
question -- see [an expense that is one person's](#an-expense-that-is-one-persons).

The patch carries what actually **moved**, not every flag you passed: the command reads the
expense first and sends the difference. Setting a field to the value it already holds is a
success that changes nothing, so the same command can be run twice.

### Before changing one

`transactions update ... --preview` shows the division the server would end up with and
changes nothing:

```bash
groupsplit tx update <id> --amount 120.00 --preview
```

Two things it is worth knowing before reading the output.

**Changing the amount alone is refused on an expense somebody split by hand** -- by the
preview and by the save alike, and that is the point of previewing. Those shares are that
person's record, nothing can work them out again, and carried onto the new total they no
longer add up to it, so the answer is `SPLITS_DO_NOT_SUM_TO_AMOUNT`. Two ways past it, and
they say different things:

```bash
groupsplit tx update <id> --amount 120.00 --split $ME=80 --split $YOU=40   # these amounts
groupsplit tx update <id> --amount 120.00 --redivide                       # the category decides
```

`--redivide` discards the shares the expense holds and divides it again by its category's
rule. It is the only way to ask for that on an expense somebody split by hand: saying nothing
about those shares keeps them, which is what stops a rename or a merchant fix from quietly
restating a division somebody made. The two contradict each other and passing both is
refused.

On an expense divided by its bill the bill follows too: `--amount` alone re-totals the bill,
the difference on its tip, and divides by it again -- the tip added on the card after the
bill was typed in is one command. The tip cannot go below nothing, so lowering the amount by
more than the bill's tip is refused with `RECEIPT_DOES_NOT_ADD_UP`; correct the bill's lines
with `receipts set` instead. Moving such an expense with `--group` clears every line's rule,
since they are the old group's rules; give the lines the new group's with `receipts rule`.

```bash
groupsplit tx update <id> --amount 58.00 --preview   # a 48.00 bill plus 10.00 of tip
```

On an expense a rule divided, `--amount` alone is accepted and the shares follow it, so
`--redivide` is not needed for that -- what it is still for there is discarding a division
somebody typed and asking for the rule's again.

**It does not divide by the rule as it reads today.** An expense that still records a version
is divided again by that version, however many times the rule has been edited since: 90.00
recorded under a two-to-one rule comes back 60.00 / 30.00 after that rule is changed to
one-to-one, not 45.00 / 45.00. Filing it under a **different** category is what reaches a
different rule, because the version it holds belongs to a rule it is no longer filed under --
so `--category-id` is the flag for "divide it by what we agreed since", not `--redivide`.

An expense somebody split by hand records no version at all: stating shares gives up the one
it had, which is the whole of what `--hand-split` says. `--redivide` on one of those has no
version to go back to and does divide by the rule as it reads now.

**The rule named in the output may not be the rule as it reads today.** An expense records
the version of the split rule that divided it, and the preview names that one; when the rule
has been edited since, the line says so and gives the date it stopped being current.

`--preview` on a settlement is refused: a settlement is one payment to one person and has no
division to show.

### Saying what divided it

Two flags record **where an expense's shares came from**, and neither moves a penny:

```bash
groupsplit tx update <id> --hand-split              # these amounts are the expense's own
groupsplit tx update <id> --divided-by <version-id> # this version of a rule worked them out
```

Version ids come from `groupsplit split-rules versions <rule-id>`.

They exist because that fact decides what a later edit does -- an expense recorded as a
rule's follows the rule when its amount or payer changes, and one whose shares are its own has
no rule to be re-billed under, so an amount edit is refused rather than restating them (unless
those shares happen to be an even division, which the app made and would make again) -- and
because a migration can only guess at it. They contradict each other, and
they contradict `--split` and `--redivide`: those two decide what the shares **become**, and
these two only describe what produced the shares already there. Any such pair is refused
before anything is sent.

Either flag alongside an ordinary edit is **two requests**: the edit, then the record of what
divided it. If the second one fails the error says so -- "the edit was saved, but recording
what divided it was not" -- and re-running the same command is the fix. It is safe: the
command reads the expense first and sends only what still differs, so the half that landed
produces no operation the second time.

## What an uncategorized expense divides by

Every group is given an "Even" rule and starts pointed at it, so an expense filed under no
category -- or under one naming no rule -- divides evenly and records the version that did
it. `groups show` says which rule that is, and the group can be pointed at another:

```bash
groupsplit groups show <group-id>                          # "Default split" names it, with its id
groupsplit split-rules list --group <group-id>
groupsplit groups set-default-split <group-id> <rule-id>
```

The one rule it will not take is the group's own "divide it by the bill": that rule reads the
lines of a receipt, so a group defaulting to it could record no expense without one. Naming
it is refused with `SPLIT_RULE_INVALID` rather than obeyed.

Moving the default changes what the **next** uncategorized expense divides by, and nothing
already recorded. An expense that already holds a rule version keeps it, so correcting the
amount of one a year later divides it by what divided it at the time -- the same promise an
expense under a category has. An expense recorded before the group had a default at all
holds no version, and the first edit to it will adopt whatever the default is then; if that
matters for a back catalogue, move the default before editing rather than after.

A single expense can still say otherwise, whatever the group's default:

```bash
groupsplit tx update <id> --split-rule <rule-id>           # this one divides by that rule
groupsplit tx update <id> --no-category                    # back to the group's default
```

## An expense that is one person's

Every group holds one rule per member that puts the whole amount on them, provisioned rather
than written -- `split-rules list` shows which member each one is for, and that column is
empty for the rules the group wrote itself:

```bash
groupsplit split-rules list --group <group-id>
```

```
 Id                                    Name              All for                               Built in
 6f1e...                               All for Anabel    0d1ac8ae-709c-4c4c-8f0f-0c7e951b3a52  yes
 9a20...                               Household 3-way   -                                     -
```

**All for** is the participant a rule puts the whole amount on, read off the division it
stands for -- a member, or a guest, who has one from the moment they are added; **built in** is whether the group was given the rule rather than
writing it.

Naming one on an expense divides the whole of it that way, whatever the category says:

```bash
groupsplit tx create "The gym" 60 --group <group-id> --split-rule <rule-id>
groupsplit tx update <id> --split-rule <rule-id>      # this one was not ours, it was Ana's
groupsplit tx update <id> --redivide                  # back to what its category says
groupsplit inbox file <row-id> --group <group-id> --split-rule <rule-id>
```

`--split-rule` is an instruction, not a field: the expense stores the division it was written
under, and there is no rule id on it to read back or clear. Saying nothing leaves it dividing
the way it divides -- through a rename, a correction to the amount, even a move to another
category -- and `--redivide` is the way back to what its category says.

Why name a rule rather than type the amount against one person with `--split`: a rule keeps
the expense divisible. Correcting the amount afterwards divides it again by the same rule,
where hand-typed shares are refused because they no longer sum to the new total. The two are
two answers to one question and passing both is refused before anything is sent.

A group that wants a division of its own in the same shape writes one, and that one is
editable like any other rule:

```bash
groupsplit split-rules create "Ana's gym" --group <group-id> --sole <user-id>
groupsplit split-rules create "Mine" --group <group-id> --payer
```

`--payer` is `--sole` with your own id, which the CLI looks up so you do not have to. What
reaches the API is the same thing either way: a rule naming a member. There is no kind that
means "whoever paid" -- a rule that named nobody was the one division that changed meaning
when the payer was corrected.

The provisioned ones are not. `split-rules update` and `split-rules delete` on one are
refused before anything is sent (`SPLIT_RULE_NOT_EDITABLE` from the API, which says the same
thing): they exist for every member without anybody creating them, and an expense may name
one the same way, so what one says is fixed.

No rule changes **kind**, provisioned or not: `split-rules update --percent` on a rule that
divides by shares is refused with `SPLIT_RULE_KIND_FIXED`. A rule is a named division, and the
shape of that division is part of what the rule is -- every category pointing at it was
pointed at a rule that divided that way. Writing a history is the exception, because it
records a past that really did change shape.

## Rewriting what a rule used to say

Editing a rule opens a new version dated now, which is right for a decision made now and no
use for a rule whose past happened in a spreadsheet. `split-rules versions set` states that
past outright, from a file:

```jsonc
// history.json -- oldest first
[
  { "from": "2023-03-01", "definition": { "$type": "shares", "shares": { "<user-id>": 3, "<user-id>": 2 } } },
  { "from": "2024-08-01", "definition": { "$type": "shares", "shares": { "<user-id>": 2, "<user-id>": 1 } } },
  { "from": "2026-01-01", "definition": { "$type": "even" } }
]
```

```bash
groupsplit split-rules versions set <rule-id> --file history.json --dry-run   # print the chain
groupsplit split-rules versions set <rule-id> --file history.json            # exit 4, then --yes
```

A `from` with no offset means midnight UTC, not midnight wherever you happen to be: these
dates are the boundaries that decide which version an expense falls under. Each entry runs
until the next one starts, so the dates must strictly increase.

Deliberately narrow, and it refuses rather than guesses:

| | |
| --- | --- |
| `SPLIT_RULE_ALREADY_HAS_HISTORY` | The rule has been through more than one version already. Only a rule that has stood for one division since it was made can have its past written wholesale. |
| `SPLIT_RULE_HISTORY_ENDS_ELSEWHERE` | The last entry is not how the rule divides today. It has to be: that entry **is** the row every recorded expense already points at, and it is reused rather than replaced. Correct the rule with `split-rules update` first. |
| `SPLIT_RULE_HISTORY_INVALID` | No entries, or dates that do not move forward. |

## Re-pointing a back catalogue

One expense at a time, with `transactions update <id> --divided-by <version-id>`. There is no
pass over a whole group any more.

There was one -- `transactions reattach` -- and it was removed because of what it had to
guess. It found each expense's rule through its category's `defaultSplitRuleId` **as the
category points now**, so a group that had since re-pointed Groceries from an even split to
one by income got three years of Groceries expenses pointed at versions of a rule that had
never divided them. Nothing records which rule a category named in 2023, so the pass could
not tell the two cases apart, and the expenses it was most useful on were the ones it was
most likely to be wrong about.

## Amounts

Every amount on the command line is written one way, on every machine: digits, a `.` for the
decimal point, **no thousands separators**, at most two decimal places. That covers the
arguments (`transactions create <name> <amount>`, `groups settle ... <amount>`), the options
(`--amount`, `--subtotal`, `--tax`, `--tip`, `--total`), the `--split` and `--percent` pairs,
and the numbers inside a receipt `--item` -- where a quantity may have three places.

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

The format does not follow the machine's language settings, and that is the point. The
parser used to: on an en-US machine `12,50` was read as 1250, and on an es-ES one `12.50`
was -- and shares misread the same way still added up to the misread total, so a $12.50
dinner could be recorded as a $1,250 one with nothing refusing it (#313). A script now means
the same amount wherever it runs.

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
[identities.md](identities.md) describes.

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

It is read back out of the parsed invocation rather than written per command, so it carries
every option that was given -- `--profile` and `--server` too, so it acts on the same account
-- and adds only `--yes`. On the way, a file is named by its absolute path, each word is
quoted for a POSIX shell when it needs to be, and a value beginning with `-` or `@` is joined
to its option with `=`. A figure the command worked out rather than was given is stated:
`settle pay` without `--amount` comes back with `--amount` set to what the prompt named.
`ConfirmCommandTests` runs every gated command both ways -- with `--yes`, and by re-running
its `confirmCommand` from another directory -- and requires the same writes from each.

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

### The skills

The four points above are the whole contract, but an agent has to be told them before its
first command rather than after its first mistake. `skills/` carries that as two agent
skills -- `groupsplit`, the protocol, and `groupsplit-inbox`, the bank-row reconciliation
loop -- installable into any agent:

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

Keycloak imports a realm on first start only, and both the local Keycloak (`WithDataVolume`)
and the deployed one keep theirs, so a realm imported before the CLI existed has no `cli`
client. The `realm-settings` worker takes care of that: on every start, locally and on every
deploy, it creates the client from this file when it is missing, and otherwise puts back its
grants (device grant on, every other grant off) and its `api` audience mapper. Nothing in the
client depends on the environment, so creating it writes exactly what an import would have.
See [Applying this to a realm that already exists](../README.md#applying-this-to-a-realm-that-already-exists).

`GROUPSPLIT_TOKEN` needs no realm change at all.
