---
name: groupsplit
description: >-
  Drive the GroupSplit CLI (`groupsplit`) for shared expenses: groups, members, expenses,
  balances, settlements, split rules, categories, receipt files, itemised receipts,
  guests and their links, linked bank accounts and imported bank rows.
  USE FOR: splitting expenses, reading balances or ledgers, recording expenses or
  settlements, itemising receipts, managing members or guests, or filing imported bank
  rows; or whenever a `groupsplit` binary or `~/.config/groupsplit/config.json` is present.
  DO NOT USE FOR: working on the GroupSplit codebase itself -- builds, tests, Aspire, EF
  migrations -- or other expense trackers.
  COVERS: the JSON contract, exit codes, the exit-4 confirmation gate, and token auth without
  a browser. ROUTES TO: groupsplit-inbox for turning imported bank rows into expenses.
license: MIT
metadata:
  author: Sussman Club
  version: "0.6.0"
---

# GroupSplit from the command line

`groupsplit` is the terminal client for GroupSplit, built to be driven by a program as much
as by a person. JSON is the default whenever stdout is not a terminal, the exit codes are a
contract, and a destructive command describes itself instead of blocking on a prompt nobody
can answer.

This skill is the protocol around the CLI. The command surface comes from the CLI itself.

## Receipts are two separate flows

The source file and the itemised bill are separate. Keep them apart, as the app does:

- `groupsplit receipts transcribe <file>` reads a local JPG, PNG, WebP or PDF into an
  editable draft. It saves no expense, receipt, attachment or split.
- `groupsplit receipts attachments upload <transaction-id> <file>` attaches a source file
  to an existing expense. `list`, `download`, `transcribe` and `delete` manage that file;
  `transcribe` returns a draft and does not save the itemised bill.
- `groupsplit inbox attachments upload <row-id> <file>` does the same for a bank row that
  is still waiting in the inbox. Filing or linking the row moves its pending files to the
  expense, but neither upload nor transcription files the row or chooses its group.
- After reviewing a transcription, use `receipts set` to save the bill. A new line given
  without `@` starts on the split rule of the expense's category (or the group's default,
  then Even, when a line cannot use it), so give `@` or use `receipts rule` only for the
  lines that divide differently. When the bill is not yet the source of the expense's shares,
  `receipts divide` stores its division. When the expense is already divided by its bill, a
  valid `set` or `rule` correction divides it again, so both ask for confirmation first. An
  incomplete correction stays editable and leaves the last valid shares in place; the call
  that completes it divides again. A forgotten tip is `transactions update --amount`: the
  bill is re-totalled, the difference on its tip. Never imply that OCR has already made
  those decisions.

`attachments delete` is destructive and uses the normal exit-4 confirmation envelope. A
download writes to a new local path and refuses to overwrite an existing file.

## Start by reading the schema

```bash
groupsplit schema --json
```

One call returns the whole command tree: every command, argument, flag, type, arity and the
exit-code table. It is generated from the tree that parses the arguments, so it matches the
version installed.

**Read it once per session and plan from it.** Do not shell out to `--help` per subcommand,
and do not pass a flag it does not list. This skill was written against one version; the
schema describes the one in front of you.

If `groupsplit` is not on `PATH`, or no server is configured, see
[references/setup.md](references/setup.md) before going further.

## The loop

1. `groupsplit schema --json` once.
2. Run each command with `--json`. It is the default when stdout is redirected, but you are
   not always the one holding the pipe.
3. Branch on the **exit code** first, then on the envelope's `code`.
4. On exit code 4, stop and show the user what would change. Do not decide for them.

```bash
groupsplit groups list --json
# --group means the group's whole ledger, whoever paid. Without it, only the caller's own
# expenses: for anybody who is not the household's main payer, a much shorter answer.
groupsplit transactions list --group <group-id> --json --fields id,name,amount,dateTime
```

`--fields` keeps only the named top-level fields, of an object or of every element of an
array. Use it; a full expense list is mostly fields you did not ask about.

## Exit codes

| Code | Meaning | What to do |
| --- | --- | --- |
| `0` | Success. stdout is trustworthy. | Parse stdout. |
| `1` | A general failure. | Read `code`; retrying may help. |
| `2` | Not signed in, expired, or refused. | See [setup.md](references/setup.md). Do not retry the same call. |
| `3` | The invocation was wrong, the server rejected a value, or no server is configured. | **Retrying will not help.** Fix the arguments. |
| `4` | A mutation needs confirming. | Surface it. See below. |
| `5` | Rate limited: too many transcriptions, bank links or syncs, or invitations for now. | Wait `retryAfter` seconds from the envelope, then run the same command once. Do not loop. |

stdout carries the result and nothing else. Progress, warnings and errors go to stderr, so
capture the two separately: a warning folded into stdout breaks your JSON parse.

## Confirming a destructive change

These actions stop unless confirmed: `groups.remove-member`, `groups.leave`,
`groups.withdraw-invitation`, `groups.settle`, `groups.settle-between`, `groups.settle-up`,
`settle.pay`, `groups.link.create` (when a link already exists), `groups.link.revoke`,
`groups.guest-link.create` (only with `--new` on a guest whose link still works),
`groups.guest-link.revoke`, `invitations.claim`, `invitations.decline`,
`transactions.delete`, `receipts.divide`, `receipts.set` and `receipts.rule` (only on a bill
the expense is divided by), `receipts.delete`, `receipts.attachments.delete`,
`inbox.attachments.delete`, `categories.delete`, `split-rules.delete`,
`split-rules.versions.set`, `bank.unlink` and `users.delete`.

With no terminal you get exit code 4 and this on stdout:

```json
{
  "confirmationRequired": true,
  "action": "groups.leave",
  "summary": "Leave the group 'Trip to Lisbon'?",
  "changes": ["You are removed from 'Trip to Lisbon' (4 members)."],
  "confirmCommand": "groupsplit groups leave 3f25... --yes"
}
```

**Show `summary` and every line of `changes` to the user, and wait for them to agree.** Then
run `confirmCommand` verbatim, not a command rebuilt from your own history. It carries every
option you gave, `--profile` and `--server` included, names files by absolute path, and is
quoted for a POSIX shell. `--fields` never narrows this envelope.

Do not put `--yes` on a command yourself. The flag is for a caller who has already been told
what the command does; adding it to save a round trip performs a deletion nobody asked for.
The one exception is a user who has said, in this conversation, to go ahead with that
specific action.

## Errors

Failures print an envelope on stderr:

```json
{
  "error": "No group with that id.",
  "code": "GROUP_NOT_FOUND",
  "remediation": "Check the id. List what you can see with: groupsplit groups list",
  "traceId": "00-a1b2c3-d4e5f6-01"
}
```

Branch on `code`, never on `error`; the prose may be reworded. A `CLI_` prefix means the CLI
raised it. Any other code came from the server, which understood the request and refused it.
Most `CLI_` codes mean nothing was sent, but **`CLI_OUTCOME_UNKNOWN` means the request may
have been carried out**: the connection broke after it left, or the server took too long.
Never repeat a change on that code without first looking for it (list or show what it would
have changed), or it may be recorded twice. Act on `remediation`. Pass `traceId` along when
reporting a problem.

## Rules

- **Never invent an id.** Every id in this system is a uuid that came from a listing. List
  first, then act on what you were given.
- **Amounts have two decimal places** and the currency is the group's. Do not round a
  user's figure to make a split come out even; use `--preview` (below).
- **Write every amount as `12.50`**: a `.` decimal point, no thousands separators, whatever
  the machine's language. `12,50`, `1,234.56`, `1.234,56` and `12.555` are refused with exit
  code 3 (`CLI_USAGE` for an argument or option, `CLI_INVALID_INPUT` inside `--split`,
  `--percent` or `--item`). When a user says "12,50 euros", convert it to `12.50` yourself;
  when they write "1.234,56", that is `1234.56`. Ask if it could be read two ways.
- **Personal is a group of one.** Every user has a personal group whose id is their user
  id; an expense with no `--group` goes there, and it can be filed under that group's own
  categories (`categories create <name> --group <user-id>`). It has no members, rules or
  settlements, and asking for one is `PERSONAL_GROUP_NOT_SHARED`.
- **Never convert a currency yourself.** A personal expense may be in any currency
  (`transactions create ... --currency EUR`, no `--group`). A group takes only its own, and
  an expense never changes currency: moving one into a group in another is refused with
  `CURRENCY_MISMATCH`. Totals come back one currency at a time (`currency`, plus
  `otherCurrencies`); report them that way and never add them up.
- **Preview a split before creating it** when the division matters:
  `groupsplit transactions create "Dinner" 84.20 --group <id> --preview` shows the split the
  server would apply and creates nothing.
- **Do not print the output of `groupsplit auth token`.** It is a bearer token, for piping
  into another tool.
- **A guest counts as a person.** A group names whoever it is sharing costs with
  (`groups add-guest`; no email and, by default, no link). From then on they can be given a
  share, be the payer and be settled with, so use their user id (the `groups members` row
  where `isGuest` is true, or `groups guests`) as you would a member's. You record their side
  of everything: "Carlos paid me back" is `groups settle` with Carlos's id. Removing one is
  `groups remove-member` once they are square, and they stay in the history as a past guest.
  A claim moves a guest's position onto the claimer's account and says what moved; report it.
- **Somebody who has left is fixed in place.** They cannot be a payer or a share on anything
  new. On an old expense they were on, renaming, re-dating and re-filing work, and so does a
  new amount with `--split` keeping their share as it is. Deleting it, re-dividing it or any
  other amount change is refused with `TRANSACTION_MOVES_DEPARTED_BALANCE`, which names them.
  Report that refusal; do not work around it. To come back they need a link or invitation
  issued after they left (`GROUP_REJOIN_NEEDS_NEW_INVITATION` otherwise). Who may do what is
  one table in `docs/participants.md`.
- **A lost link is recoverable only if it was opened.** `invitations list` returns the guest
  links this account has opened. There is no address to match, so a link never followed is
  unknown to it. If the user never opened theirs, a member can print it again with
  `groups guest-link create`.
- **A guest's link expires 30 days after it is issued.** Expired, it is the same 404
  (`GROUP_INVITATION_NOT_FOUND`) as a claimed one, and `groups guests` marks it expired.
  `groups guest-link create` issues a new one. The old URL stays dead, so the new one has to
  be sent again.
- **A guest's link is a credential, not an id.** Whoever opens one takes on the shares
  recorded for that guest, so it belongs to the person the user is sending it to and
  nowhere else. Do not paste one into a shared channel, a commit, an issue, or a transcript
  you did not have to write it into, and do not confuse it with `groups link`, which is the
  group's open door and claims nothing.
- **Settle with a person, not a group.** `groupsplit settle plan` answers "how do I clear
  this?" It adds up every group's balance per person, so one payment clears a friend you owe
  in two places. `groups settle` is still right when the user means one group.
- **Settling a period is `--through <day>`, not arithmetic.** "Settle August" is
  `settle plan --through 2026-08-31` (or `groups settle-up <group-id> --through ...`). It
  counts expenses up to the day and the repayments made for it, and later repayments only as
  far as they pay it down, so nothing already paid is asked for twice. Summing a month's
  expenses yourself leaves the repayments out; do not present that as what is owed.
- **`groupsplit config list`** answers what the *next* command will talk to, and says which
  source each value came from. Run it when a request went somewhere unexpected.
- **Know whose expenses a listing covers before you report a total.** `transactions list`
  and `transactions summary` are the caller's own expenses; `--group` switches both to the
  group's whole ledger, whoever paid. `transactions monthly` and the two
  `transactions shares` commands stay the caller's either way, and there `--group` only
  narrows. One flag name covers both behaviours, so read its description in the schema for
  each command.
- **Never total a group's spending from an expense listing without saying what is missing.**
  Settlements are a different transaction type and appear in none of these commands, so
  every figure they give is gross. `groupsplit groups activity <group-id>` is everything a
  group did, transfers included; `groupsplit users position` is where somebody stands.
- Set `GROUPSPLIT_DEBUG=1` to attach a stack trace to an unexpected error. An envelope with
  code `CLI_INTERNAL_ERROR` is a defect in the CLI, not a usage mistake: report it; do not
  work around it.

## Where to look next

| You need | Read |
| --- | --- |
| The full command surface, and the flag formats that are not obvious | [references/commands.md](references/commands.md) |
| Installing, pointing at a server, signing in, profiles, the local stack | [references/setup.md](references/setup.md) |
| The machine contract in full: output, envelopes, the error-code catalog | [references/protocol.md](references/protocol.md) |
| Turning imported bank rows into expenses without recording anything twice | the **groupsplit-inbox** skill |
