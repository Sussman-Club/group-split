---
name: groupsplit-inbox
description: >-
  Turn imported bank rows into GroupSplit expenses without recording the same money twice.
  USE FOR: the GroupSplit inbox, imported or synced bank rows, filing a bank row as an
  expense, reconciling a card statement against what a group already recorded, a
  POSSIBLE_DUPLICATE_EXPENSE refusal, `groupsplit inbox`, `groupsplit bank`, pending receipt
  attachments, or
  `groupsplit transactions bank-matches`.
  DO NOT USE FOR: recording an expense somebody typed (that is `transactions create`),
  balances, settling up, or anything else in the CLI -- use the groupsplit skill.
  REQUIRES: the groupsplit skill for the JSON contract, exit codes and the confirmation
  protocol. Read it first.
license: MIT
metadata:
  author: Sussman Club
  version: "0.3.0"
---

# Filing bank rows

Two paths put an expense in the ledger: somebody types one, or somebody files a row the bank
sent. This skill stops both happening for the same money.

Somebody records the dinner at the table so nobody forgets it. The card charge lands two days
later and is filed from the inbox. The group now holds the same dinner twice, in every
balance and every total, and nothing anywhere says so. Whoever paid appears to be owed twice
what they are, and the only way to notice is to read the history line by line.

**Nothing here merges, hides, files or ignores anything on its own.** Every step waits for a
person. Put the choice in front of them with enough information to make it, and do not make
it for them.

Read the **groupsplit** skill first: the JSON contract, the exit codes and the confirmation
protocol all apply here unchanged.

## Attach a receipt before filing

A pending bank row may carry a source receipt while the person is still deciding which
group and split rule it belongs to:

```bash
groupsplit inbox attachments upload <row-id> ./receipt.pdf
groupsplit inbox attachments list <row-id> --json
groupsplit inbox attachments transcribe <row-id> <attachment-id> --json
```

Uploading and transcribing do not file the row, choose a group, save an itemised bill, or
change shares. The transcription is a draft for review. Once the row is filed or linked,
the pending attachments move to the resulting expense; use `groupsplit receipts attachments`
and `groupsplit receipts set` there to continue. `inbox attachments delete` is confirmation
gated, and `inbox attachments download` writes a copy to a new local path.

A file attached to a *pending* row is not lost when the charge posts: it moves to the
posted row that takes over, so list the attachments on the row the inbox shows now. A file
on a row the bank withdraws, or on a row whose bank is unlinked, is deleted with the row.
Say so before somebody unlinks a bank with receipts still waiting in its inbox.

## The loop

```bash
groupsplit inbox summary --json                    # how many are waiting, and how many look filed already
groupsplit inbox list --json                       # what they are
groupsplit inbox matches <row-id> --json           # what each could already be
```

`inbox summary` returns `newCount` and `possibleDuplicates`, the number of waiting rows that
agree to the cent with an expense somebody has already recorded. Lead with it: a backlog
where none are duplicates is a different job from one where a third are. Pass
`--duplicates false` to skip that count for a cheaper read; the count runs the matcher over
every waiting row.

That count covers only the `Confident` matches. `inbox list` marks the rest (see
[How sure a suggestion is](#how-sure-a-suggestion-is)), and filing is refused over both, so
`possibleDuplicates` being zero does **not** mean every row will file cleanly.

For each row, in order:

1. **`inbox matches <row-id>`** first, always, before filing anything. It is a read and
   changes nothing. At most three candidates come back, each with `transactionId`, `name`,
   `amount`, `daysApart`, `amountDifference` and `confidence`, so a person can see *why* it
   was offered instead of taking it on trust.
2. **Nothing came back** → file it. `groupsplit inbox file <row-id> [--group <id>]
   [--category-id <id>]`.
3. **Something came back** → show the candidates to the user with their amounts and how far
   apart they are, and ask. There are three answers, each with its own command:

| The user says | Command | What it does |
| --- | --- | --- |
| "That's the same payment" | `groupsplit inbox link <row-id> <transaction-id>` | Attaches the row to the expense that is already there. No second expense. |
| "I really did pay twice" | `groupsplit inbox file <row-id> --file-anyway` | Records it anyway. |
| "That's not it" | `groupsplit inbox dismiss-match <row-id> <transaction-id>` | Never suggests that pair again. The row stays waiting. |

Do not pick for them. The three are not interchangeable, and the wrong one does not look
wrong afterwards.

## A charge is one expense

One card charge files as one expense. A warehouse run that was the flat's groceries *and* a
jacket of the payer's is still one charge here: file it where most of it belongs, and correct
the rest with an ordinary expense. The API cannot split a charge into several expenses, so do
not offer it.

A bill on the expense answers who owes what within it (see the `groupsplit` skill's
`receipts` reference). Type the bill against the expense the row became, not against the
row.

## How sure a suggestion is

Every candidate carries a `confidence`. It changes how you present the candidate, not
whether you do.

| `confidence` | What the rule found | How to say it |
| --- | --- | --- |
| `Confident` | The amounts agree to the cent | Lead with it. Measured against real spending, two unrelated expenses by one person agree this closely 0.18% of the time, so this is very probably the same money. |
| `Possible` | The amounts differ by a tip, or by a figure somebody rounded | Mention it as a question, not a finding. It is right often enough to be worth asking and wrong often enough that presenting it as a duplicate is misleading. |

**Both are refused a filing.** A tip added to a dinner is the commonest real duplicate and
is never `Confident`, so do not skip past a `Possible` match. Take one of the three answers
for either grade.

Do not flatten the two grades. Telling somebody a streaming charge "is already recorded" when
the rule only found a figure within a quarter of it teaches them to wave suggestions
through, genuine duplicates included.

## The refusal

Filing a row that looks like an expense already recorded fails with exit code 3 and

```json
{ "code": "POSSIBLE_DUPLICATE_EXPENSE", "error": "..." }
```

before a second expense exists, and the refusal names what it matched. Treat it as a
question: go back to `inbox matches`, show the candidates, and take one of the three answers
above.

**`--file-anyway` is not a `--yes`.** `--yes` skips a confirmation prompt; `--file-anyway`
asserts a fact, that the same amount was paid twice to the same merchant within days. Never
add it to get past a refusal. Add it only when a user has told you, in this conversation,
that they paid twice.

## What linking does and does not do

Linking leaves one expense with the bank's row attached to it (the same field filing would
have written) and **does not change the amount, the date or the division**. What somebody
wrote down stays what they wrote down. A card that settled for six more than the typed
figure is not a correction anybody asked for. If the user wants it corrected, that is
`groupsplit transactions update <transaction-id> --amount <new>` afterwards, as a separate
decision they made. On shares somebody typed, `--amount` alone is refused
(`SPLITS_DO_NOT_SUM_TO_AMOUNT`); give the new shares with `--split`, or `--redivide`. See
[Setting exact shares](../groupsplit/references/commands.md#setting-exact-shares---split).

## What filing copies

`inbox file` copies what the bank already said: the date, the amount, the currency. Those
have no flags here. Correcting the bank is `transactions update` afterwards.

The currency is kept whichever way it is filed. A row kept personal (no `--group`) stays in
the bank's currency: a 20.00 EUR charge is a 20.00 EUR expense. A group takes only its own,
so filing a euro row into a dollar group is refused with `CURRENCY_MISMATCH`, and so is
moving it there afterwards with `transactions update --group`. Nothing converts. Offer to
keep it personal; do not re-type it in dollars.

What you may set while filing:

| Flag | |
| --- | --- |
| `--group <group-id>` | Which group. **Omit and it is personal** -- in the user's personal group, shared with nobody. |
| `--category-id <category-id>` | Its rule is what divides the expense. Kept personal, a category of the personal group (whose id is the user's) files it with nothing to divide. |
| `--paid-by <user-id>` | Who paid, if not the user. |
| `--split-rule <rule-id>` | Divide the whole row by this rule whatever the category says. A group holds one per member that puts all of it on them -- which is what "that one was not ours, it was Ana's" comes to on a card statement. Ids from `split-rules list --group <group-id>`, where the **all for** column names the member. |
| `--split <user-id>=<amount>` | Exact shares, repeatable. Omit and the category's rule divides it. Refused alongside `--split-rule`: they are two answers to one question. |
| `--name`, `--description` | A readable name in place of `SQ *TRATTORIA 4421`. |
| `--file-anyway` | See above. |

Filing into no group is a decision, not a fallback. When you are unsure which group a row
belongs to, ask.

## The other direction

An expense typed *after* the bank row arrived is the same problem the other way round:

```bash
groupsplit transactions bank-matches <transaction-id> --json
```

It returns the rows still waiting that could be the expense just recorded. Run it after
creating an expense in a group whose bank account is linked, and take the same three
answers.

The two directions ask about different rows. Filing is checked against any row that can
still be filed, **including an ignored one**; filing refuses only a row that is already an
expense. Suggestions in this direction offer only rows that are still **waiting**, because a
row somebody put away is not waiting for anything.

## Putting a row away

`groupsplit inbox ignore <row-id>` takes a row out of the waiting list;
`groupsplit inbox restore <row-id>` puts it back. Neither is destructive and neither is
confirmation-gated. Ignoring is for a row that is not a group expense at all (a personal
purchase, a bank fee), not for a row you could not work out.

`groupsplit inbox list --status filed|ignored|new` reads back each pile.

## Undoing a filing

To undo a row filed into the wrong group, or filed when it should have been linked, delete
the expense (`groupsplit transactions delete <transaction-id>`, confirmation-gated). The row
goes back to the inbox as waiting, carrying the receipt files it had there before filing,
and can be filed, linked or ignored again. Files added to the expense afterwards go with the
expense. A row the bank has withdrawn since it was filed has nothing to go back to and is
deleted with the expense.

`inbox ignore` on a row that is still an expense refuses with
`BANK_TRANSACTION_ALREADY_FILED`. Delete the expense instead.

## When the matcher fires

Use this to explain a suggestion, or its absence. Two conditions have to hold before
anything is compared:

- **The payer is the same person.** A bank row is one person's card charge, so the only
  expenses it can be are ones they are down as having paid.
- **The expense has no bank row already**, or only one the bank withdrew or that a posted
  row superseded. A pending row filed as an expense and then re-posted under a new id
  without naming it arrives as a fresh row; it is matched against that expense, and
  `inbox link` moves the expense onto it.

Then the dates must be within 5 days of each other, and the amounts within 25% or 1.00,
whichever is more generous. A card charge posts days after the meal, and a tip added after
the receipt was written is the usual reason two amounts differ. At most 3 candidates are
offered.

Money in another currency is not the same money, and money coming *in* (a refund, a
deposit) has no expense to be a duplicate of. The bank's merchant text is not a condition:
"Dinner" against `SQ *TRATTORIA 4421` is the ordinary case, so a name test would rule out
most real duplicates. It only breaks a tie between two otherwise equal candidates.

None of this catches duplicates within one side, such as two typed expenses for the same
dinner or two bank rows for it. It only compares typed expenses against bank rows.

## Getting rows in

```bash
groupsplit bank list --json                 # linked accounts and their state
groupsplit bank sync <connection-id>        # pull new rows
groupsplit bank refresh <connection-id>     # re-read the accounts, then pull
```

`BANK_CONNECTION_NEEDS_ATTENTION`, and a connection reading `login required`, mean the bank
needs the user: a new sign-in, a locked account, a password reset, an agreement waiting on
the bank's own site, no account shared. There is nothing to retry. The user deals with
whatever the bank asks, then signs in again through update mode
(`bank link-token --connection <id>`, then `bank refresh`). `bank link` and `bank unlink` are
the user's to run; `bank unlink` is confirmation-gated, and succeeds even when the provider
has already let go of the item.

A connection reading `account not shared` has money the app is not importing: the bank has
an account the user never shared. Only they can fix it, in the linking UI in update mode
with that account ticked, then `bank refresh`. When rows are missing from the inbox with no
error anywhere, check for this first.
