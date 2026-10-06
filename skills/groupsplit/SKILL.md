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

`groupsplit` is the terminal client for GroupSplit. It was built to be driven by a program
as much as by a person: JSON is the default whenever stdout is not a terminal, the exit
codes are a contract, and destructive commands describe themselves rather than blocking on
a prompt nobody can answer.

Everything below is the protocol around the CLI. The CLI's own surface comes from the
CLI, not from here.

## Receipts are two separate flows

The CLI exposes both the source file and the itemised bill. Keep them separate in the same
way the app does:

- `groupsplit receipts transcribe <file>` reads a local JPG, PNG, WebP or PDF into an
  editable draft. It saves no expense, receipt, attachment or split.
- `groupsplit receipts attachments upload <transaction-id> <file>` attaches a source file
  to an existing expense. `list`, `download`, `transcribe` and `delete` manage that file;
  `transcribe` returns a draft and does not save the itemised bill.
- `groupsplit inbox attachments upload <row-id> <file>` does the same for a bank row that
  is still waiting in the inbox. Filing or linking the row moves its pending files to the
  expense, but neither upload nor transcription files the row or chooses its group.
- After reviewing a transcription, use `receipts set` to save the bill and `receipts rule` to
  assign each line's split rule. `receipts divide` explicitly stores the division when the
  bill is not already the source of the expense's shares. If an itemised receipt already
  drives the stored ledger, a valid `set` or `rule` correction keeps those shares in sync;
  an incomplete correction remains editable and leaves the last valid shares in place.
  Never imply that OCR has already made those decisions.

`attachments delete` is destructive and uses the normal exit-4 confirmation envelope. A
download writes to a new local path and refuses to overwrite an existing file.

## Start by reading the schema

```bash
groupsplit schema --json
```

One call returns the whole command tree -- every command, argument, flag, type, arity and
the exit-code table. It is generated from the same tree that parses the arguments, so it
cannot describe a CLI that does not exist, and it is right for the version that is actually
installed.

**Read it once per session and plan from it.** Do not shell out to `--help` per subcommand,
and do not pass a flag it does not list: this skill was written against one version and the
schema is the one that is in front of you.

If `groupsplit` is not on `PATH`, or no server is configured, see
[references/setup.md](references/setup.md) before going further.

## The loop

1. `groupsplit schema --json` once.
2. Run each command with `--json`. It is the default when stdout is redirected, but say it
   anyway -- you are not always the thing holding the pipe.
3. Branch on the **exit code** first, then on the envelope's `code`.
4. On exit code 4, stop and show the user what would change. Do not decide for them.

```bash
groupsplit groups list --json
# --group means the group's whole ledger, whoever paid. Without it, the caller's own
# expenses -- which is a different question and, for anybody who is not the household's
# main payer, a much shorter answer.
groupsplit transactions list --group <group-id> --json --fields id,name,amount,dateTime
```

`--fields` keeps only the named top-level fields, of an object or of every element of an
array. Use it: a full expense list is mostly fields you did not ask about.

## Exit codes

| Code | Meaning | What to do |
| --- | --- | --- |
| `0` | Success. stdout is trustworthy. | Parse stdout. |
| `1` | A general failure. | Read `code`; retrying may help. |
| `2` | Not signed in, expired, or refused. | See [setup.md](references/setup.md). Do not retry the same call. |
| `3` | The invocation was wrong, or no server is configured. | **Retrying will not help.** Fix the arguments. |
| `4` | A mutation needs confirming. | Surface it. See below. |

stdout carries the result and nothing else. Progress, warnings and errors are on stderr, so
capture the two separately -- a warning folded into stdout will break your JSON parse.

## Confirming a destructive change

These actions stop unless confirmed: `groups.remove-member`, `groups.leave`,
`groups.withdraw-invitation`, `groups.settle`, `groups.settle-between`, `groups.settle-up`,
`settle.pay`, `groups.link.create` (when a link already exists), `groups.link.revoke`,
`groups.guest-link.create` (only with `--new` on a guest who has a link),
`groups.guest-link.revoke`,
`invitations.claim`, `invitations.decline`, `transactions.delete`, `receipts.divide`,
`receipts.delete`, `receipts.attachments.delete`, `inbox.attachments.delete`,
`categories.delete`, `split-rules.delete`, `split-rules.versions.set`, `merchants.update`
(only when it renames -- a merchant is shared by every group that has spent there, so the
prompt carries the count), `merchants.delete`, `bank.unlink` and `users.delete`.

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
run `confirmCommand` verbatim -- it is the exact invocation, so you never have to
reconstruct one from your own history.

Do not put `--yes` on a command yourself. The flag exists for a caller who has already been
told what it does; reaching for it to avoid a round trip performs a deletion nobody asked
for. The one exception is a user who has said, in this conversation, to go ahead with that
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

Branch on `code`, never on `error` -- the prose may be reworded. A `CLI_` prefix means the
CLI refused before any request was sent; anything else came from the server and means the
request was understood and refused. `remediation` is written to be acted on. Pass `traceId`
along when reporting a problem.

## Rules

- **Never invent an id.** Every id in this system is a uuid that came from a listing. List
  first, then act on what you were given.
- **Amounts have two decimal places** and the currency is the group's. Do not round a
  user's figure to make a split come out even -- `--preview` instead, below.
- **Preview a split before creating it** when the division matters:
  `groupsplit transactions create "Dinner" 84.20 --group <id> --preview` shows the split the
  server would apply and creates nothing.
- **Do not print the output of `groupsplit auth token`.** It is a bearer token. It is for
  piping into another tool, not for a transcript.
- **A guest counts as a person.** A group names whoever it is sharing costs with
  (`groups add-guest`) -- there is no email and, by default, no link -- and from that moment
  they can be given a share, be the payer, and be settled with, so use their user id (the
  `groups members` row where `isGuest` is true, or `groups guests`) as you would a member's.
  You record their side of everything: "Carlos paid me back" is `groups settle` with
  Carlos's id. Removing one is `groups remove-member` once they are square, and they stay in
  the history as a past guest. A claim moves a guest's position onto the claimer's account
  and says what moved -- report it.
- **Somebody who has left is fixed in place.** They cannot be a payer or a share on anything
  new. On an old expense they were on, renaming, re-dating and re-filing work, and so does a
  new amount with `--split` keeping their share exactly as it is; deleting it, re-dividing
  it or any other amount change is refused with `TRANSACTION_MOVES_DEPARTED_BALANCE`, which
  names them. Report that refusal -- do not work around it. To come back they need a link or
  invitation issued after they left (`GROUP_REJOIN_NEEDS_NEW_INVITATION` otherwise). Who may
  do what is one table in `docs/participants.md`.
- **A lost link is recoverable only if it was opened.** `invitations list` answers the
  guest links this account has opened -- there is no address to match, so nothing knows
  about one that was never followed. If the user never opened theirs, a member can print it
  again with `groups guest-link create`.
- **A guest's link is a credential, not an id.** Whoever opens one takes on the shares
  recorded for that guest, so it belongs to the person the user is sending it to and
  nowhere else. Do not paste one into a shared channel, a commit, an issue, or a transcript
  you did not have to write it into, and do not confuse it with `groups link`, which is the
  group's open door and claims nothing.
- **Settle with a person, not a group.** `groupsplit settle plan` is the answer to "how do
  I clear this?": it adds every group's balance up per person, so one payment clears a
  friend you owe in two places. `groups settle` is still right when the user means one
  group specifically.
- **Settling a period is `--through <day>`, not arithmetic.** "Settle August" is
  `settle plan --through 2026-08-31` (or `groups settle-up <group-id> --through ...`). It
  counts expenses up to the day and every repayment whatever its date, so nothing already
  paid is asked for twice. Summing a month's expenses yourself leaves the repayments out --
  do not present that as what is owed.
- **`groupsplit config list`** answers what the *next* command will talk to, and says which
  source each value came from. Run it before wondering why a request went somewhere
  unexpected.
- **Know whose expenses a listing covers before you report a total.** `transactions list`
  and `transactions summary` are the caller's own expenses; `--group` switches both to the
  group's whole ledger, whoever paid. `transactions monthly` and the two
  `transactions shares` commands stay the caller's whichever way, and there `--group` only
  narrows. The flag's own description on each command says which it does -- read it in the
  schema rather than assuming, since one flag name covers both behaviours.
- **Never total a group's spending from an expense listing without saying what is missing.**
  Settlements are a different transaction type and appear in none of these commands, so
  every figure they give is gross. `groupsplit groups activity <group-id>` is everything a
  group did, transfers included; `groupsplit users position` is where somebody actually
  stands. Reporting a group's spend as though it were net is the mistake this CLI is
  shaped to make easy.
- Set `GROUPSPLIT_DEBUG=1` to attach a stack trace to an unexpected error. An envelope with
  code `CLI_INTERNAL_ERROR` is a defect in the CLI, not a usage mistake -- report it rather
  than working around it.

## Where to look next

| You need | Read |
| --- | --- |
| The full command surface, and the flag formats that are not obvious | [references/commands.md](references/commands.md) |
| Installing, pointing at a server, signing in, profiles, the local stack | [references/setup.md](references/setup.md) |
| The machine contract in full: output, envelopes, the error-code catalog | [references/protocol.md](references/protocol.md) |
| Turning imported bank rows into expenses without recording anything twice | the **groupsplit-inbox** skill |
