# The same expense arriving twice

Two paths put an expense in the ledger: somebody types one, and somebody files a row the
bank sent. Until this, nothing connected them.

The ordinary case went wrong quietly. Somebody records the dinner at the table so nobody
forgets it. The card transaction lands two days later. They file it from the inbox, and the
group holds the same dinner twice: counted twice in every balance, twice in every total, and
nothing anywhere saying so. Whoever paid appears to be owed twice what they are, and the only
way to notice is to read the history line by line.

What this adds is a suggestion and two answers to it. Nothing is merged, hidden, filed or
ignored without a person choosing it.

## What is true now

- A match carries a **grade**, and the grade is what decides how loudly it is said. See
  [Two grades](#two-grades) -- everything below applies to both unless it says otherwise.
- Filing an imported row that looks like an expense already recorded is refused with
  `POSSIBLE_DUPLICATE_EXPENSE`, **before** a second expense exists, and the refusal names
  what it matched.
- The person decides. `FileAnyway` on the file request records it regardless -- they really
  did pay twice -- and `POST /inbox/{id}/link` points the row at the expense that is already
  there instead.
- Linking leaves one expense with the bank's row attached to it: the same
  `Transaction.BankTransactionId` filing writes, and no change to the amount, the date or the
  division. What somebody wrote down stays what they wrote down; a card settling for six more
  is not a correction anybody asked for.
- The other order is caught as well. `GET /transactions/{id}/bank-matches` answers with the
  rows still waiting that could be an expense just recorded, and the client asks it the
  moment one is saved.
- A dismissed pair is never raised again: `POST /inbox/{id}/dismiss-match` writes a
  `BankMatchDismissal` row, and every suggestion query filters through it. A suggestion
  nobody can get rid of is worse than none.

## Two grades

The first version of this treated everything that passed the amount-and-date test as one
claim, and [#212](https://github.com/Sussman-Club/group-split/issues/212) measured what that
cost. Over the 1,414 real expenses in one group, **11.5% of unrelated pairs by the same
payer** pass the amount test on their own -- and a person types between 1.5 and 7 expenses
into the eleven days the rule looks at, so between a fifth and well over half of arriving
rows raised a suggestion about money they had nothing to do with. On the demo data the score
was two suggestions, both wrong, and no real duplicate anywhere to be caught.

Exactness is what separates the two claims:

| Grade | What it means | Coincidence rate | What it does |
| --- | --- | --- | --- |
| `Confident` | The amounts agree **to the cent** | 0.18% of pairs | A warning on the row, and a refusal on filing |
| `Possible` | They differ by something a duplicate could explain | 3.9% of pairs | A quiet note, and a refusal on filing |

**The refusal is deliberately not graded.** A tip added to a dinner is the likeliest
duplicate there is, and it is only ever a possible match: a guard that wanted a confident one
would wave the commonest case straight through, which is the failure this exists to prevent.
What the grade decides is how loudly a suggestion is announced to somebody who has not
touched the row -- not whether the same money can be recorded twice without a word.

### What shape a difference is allowed to have

The tolerance is there for a tip, and a tip only ever makes the card charge **larger** than
the figure somebody wrote down. Reading it in both directions cost half of everything the
rule raised, and both false suggestions the seeded inbox used to produce were of the refused
shape -- a bank charging *less* than the expense it was offered against.

Two things do explain a charge coming in lower, and both are allowed:

- **A round figure.** People round when they type, so an expense on a whole unit may sit
  above the charge and still be the same money. 12% of real expenses are whole units, so
  this costs about a third of a percentage point.
- **A pending row.** A pending authorisation is often taken before the tip is added, so for
  a pending row the lower charge is the expected direction rather than the suspicious one.

### The merchant is a condition now, in one direction

Both an imported row and an expense carry the place the money went, as a link to one shared
`Merchant`. Both sides naming a place and naming **different** ones rules out a *possible*
match: the streaming subscription the seed used to offer against a coffee run is settled by
this and by nothing else in the rule.

It does **not** rule out a confident one. One shop can end up as two merchants -- a chain and
its subscription arm, a shop and its parent -- and refusing a real duplicate is the expensive
mistake. An exact amount overrules a merchant that disagrees.

One side knowing and the other not says nothing: most expenses somebody types have no
merchant and never will, so silence there is not disagreement.

Agreeing on the place is also a **ranking** signal, and it outranks a nearer amount. Ordering
by closeness alone let a coincidence a few cents nearer outrank the genuine duplicate with a
tip on it -- and since a row leads with its best candidate, that did not merely mis-sort a
list, it kept the real match off the screen.

## Where the rule lives

`DuplicateMatcher`, in `src/GroupSplit.API/Services/Banking/DuplicateMatching.cs`, and
nowhere else. Callers ask for candidates and say what a person chose; none of them knows what
the window is. Tuning it is editing four constants at the top of that file.

| Constant | Value | Why |
| --- | --- | --- |
| `WindowDays` | 5 | A card charge posts days after the meal, and a weekend one waits until Tuesday. A window that only caught the same day would miss most real duplicates. |
| `RelativeTolerance` | 25% | A tip added after the receipt was written is the usual reason the two amounts differ. Decides nothing on its own: see the shape rules above. |
| `AbsoluteTolerance` | 1.00 | So a €4 coffee is not held to a tolerance of pennies. Whichever of the two is more generous applies. |
| `MostSuggestions` | 3 | Somebody choosing between five candidates is not being helped by any of them. |

Two things have to hold before those numbers are consulted at all, and they are conditions on
the *sets* rather than on the pair:

- **The payer is the same person.** A bank row is one person's card charge, so the only
  expenses it can be are ones they are down as having paid. Somebody else's card charge lands
  in somebody else's inbox.
- **The expense has no bank row already.** An expense carries at most one, and one that has
  one is not waiting to be matched against another.

Money in another currency is not the same money, and money coming in -- a refund, a deposit
-- has no expense to be a duplicate of.

The two directions ask about different rows, and deliberately so. Filing is checked for any
row that can still be filed, which includes an **ignored** one: filing refuses only a row
that is already an expense, so a guard that asked about waiting rows alone would let an
ignored row through it. The other direction offers only rows that are **waiting** -- a row
somebody put away is not waiting for anything, and handing it back unasked is the suggestion
nobody can get rid of.

**The bank's merchant *text* is still not a condition.** "Dinner" against
`SQ *TRATTORIA 4421` is the ordinary case, so a name test would refuse most real duplicates.
It is the last tiebreak between otherwise equally close candidates and nothing more -- what
*is* a condition is the resolved merchant, which is a different thing and is above.

## How it is asked

Matching runs in memory over a small candidate set, rather than as a join the database
evaluates. The set is bounded by the date window and the caller, so it is a handful of rows
however it is filtered -- and the arithmetic that decides a person's balance stays somewhere
it can be read, rather than in a predicate written for the translator.

The inbox listing asks once for the whole page, after the paging, and hands each waiting row
what it could already be. A query per row would be twenty-five round trips to say something
that is the same question every time.

## Pending → posted is a different problem

A posted row superseding its pending one already has its own answer, in
[the Phase 3 plan](phase-3-plaid-integration.md). That is one bank row replacing another and
is decided by the provider's own `pending_transaction_id`; this is a bank row and something a
person typed, and is decided by nobody until they say so.

They can both move the same row, so they do not overlap: a superseded row is never listed,
never filed and never suggested, and supersession takes over the pending row's status --
including a `Filed` one, whose expense link moves with it. A row that a person attached to an
expense is `Filed`, so it takes that path and not this one.

## What the clients do with a grade

Both surfaces say the same two things in their own vocabulary, so nobody learns them twice.

| | `Confident` | `Possible` |
| --- | --- | --- |
| Inbox | A warning on the row, with both answers | The same block in a plain voice, saying how far apart they are |
| Inbox select-all | Left out | Left out -- filing is refused over both |
| Nav badge, `inbox summary` | Counted | Not counted |
| `inbox list` | `(duplicate?)` | `(similar)` |
| `inbox matches` | `(same amount)` | `(similar amount)` |

Every candidate is listed rather than only the best-ranked one, on both surfaces.

## What is still out

- **Merging anything automatically.** Everything here waits for somebody to press something.
- **Merchant rules and auto-filing**, tracked separately.
- **Duplicates within one side.** Two typed expenses for the same dinner, or two bank rows
  for it, are not what this looks for; it is the seam between the two paths that had nothing
  watching it.
