# Phase 1: reshaping the model

The implementation plan for [Phase 1 of the roadmap](roadmap.md#phase-1----reshape-the-model-3-weeks).
The roadmap settles *what* the model becomes and why; this settles the order it is built
in, how the existing rows get there, what is deleted on the way, and which tests change.

Read [The target model](roadmap.md#the-target-model), [Settlements as
transfers](roadmap.md#settlements-as-transfers) and [Category and
split](roadmap.md#category-and-split) first. Nothing here re-argues them.

## Scope

In:

- `Transaction` as an abstract TPH base with `Expense` and `Transfer` leaves.
- `TransactionSplit` rows, written at create and edit, summing to the amount.
- Nullable `Transaction.GroupId` -- personal is the absence of a group, not a hidden one.
- `SplitRule` as an editable template; `Category` as a label that may name a default rule.
- `Currency` on the group and on the transaction.
- The balance query rewritten as two sums.
- The TPT rule-version hierarchy, its handlers, `RuleFlags` and `RuleFilter` deleted.
- The affected tests rewritten against behaviour.

Out, deliberately:

- **`PATCH` -> `PUT`, and dropping the pinned JSON Patch package.** Held back on request.
  JSON Patch stays exactly as it is; what that costs the new model is worked out in
  [Keeping JSON Patch](#keeping-json-patch), because it is not free and the cost lands on
  the one invariant everything else rests on.
- Invitations, roles and pending members. They are Phase 2, with the feature that reads
  them. `GroupMembership` gains no speculative columns here -- see
  [Decisions taken](#decisions-taken).
- Anything on the bank side. `BankTransactionId` is not added yet either: it has no
  writer until Phase 3, and a nullable column with no writer is a comment with a table
  behind it.

## Decisions taken

The roadmap left four questions open. Two of them block this phase, so they are answered
here rather than deferred.

**Rule history is dropped, with no replacement.** No `SplitRuleRevision`, no versions,
no `StartDateTime`/`EndDateTime`. The history that matters -- what each person actually
owed on each expense -- is on the transaction as splits, and is therefore more accurate
than the version chain was: it survives a rule edit, which is precisely what versioning
was trying and failing to guarantee. A revision log can be added later without touching
a single transaction, because no transaction will point at it. Adding it now would be
the third thing built on a question nobody has asked.

**Currency lives in both places, and they must agree.** `Group.Currency` and
`Transaction.Currency`, both ISO 4217, both required, defaulting to `USD` on migration.
An expense whose currency differs from its group's is refused with `409
CurrencyMismatch`. Conversion is out of scope, and a group that silently mixed currencies
would produce balances that are wrong rather than merely unavailable. A personal
transaction (no group) may carry any currency; nothing sums across personal transactions
yet.

Two consequences worth naming: this is the shape Plaid needs later (it hands you a
currency per transaction, so the column has to exist), and the refusal is a guard on a
seam that is otherwise silent, so it gets a test rather than a comment.

**`GroupMembership` gains nothing in this phase.** It has `ArchivedAt` from Phase 0 and
that is enough for the model to be correct. `Role`, `Status`, `InvitedEmail`, `JoinedAt`
and `LeftAt` arrive in Phase 2 alongside invitations, which is the only code that would
read them. The roadmap lists them in the target-model table because that table describes
the destination, not this phase.

**Leaving a group stays guarded, and the guard changes hands.** Today
`RuleVersionReferencesRemovedMember` refuses a write whenever a rule version names
somebody no longer in the group -- a guard that exists only because the split lives on
the rule. Splits on the transaction are historical facts, so a departed member's past
splits stay valid and the guard becomes meaningless; it and `RuleVersionHasRemovedMember`
are deleted. What must not be lost is the reason it was reached for: removing a member
who is owed money. That becomes an explicit check on removal -- non-zero net balance
refuses with `409 MemberHasBalance` -- which is a rule about people, states what it means,
and is a third of the code.

## The schema

One level of inheritance, shared columns on the base, discriminator indexed. The
guardrails from [Why TPH](roadmap.md#why-tph-given-what-tpt-did-to-rule-versions) are
mapping rules here, not aspirations.

```csharp
public abstract class Transaction : Entity
{
    public Guid? GroupId { get; set; }              // null = personal
    public virtual Group? Group { get; set; }

    public Guid PaidByUserId { get; set; }
    public virtual User PaidBy { get; set; } = null!;

    public required decimal Amount { get; set; }    // > 0, two decimal places
    public required string Currency { get; set; }   // ISO 4217, 3 chars
    public required DateTimeOffset Date { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    public virtual ICollection<TransactionSplit> Splits { get; } = [];
}

public sealed class Expense : Transaction
{
    public Guid? CategoryId { get; set; }
    public virtual Category? Category { get; set; }
}

public sealed class Transfer : Transaction
{
    // The single split is the whole point, so it cannot be forgotten: nothing
    // outside this type may construct one.
    public static Transfer Between(Guid groupId, User from, User to, decimal amount,
        string currency, DateTimeOffset date) { /* one split, to `to`, for `amount` */ }
}

public class TransactionSplit : Entity
{
    public Guid TransactionId { get; set; }
    public Guid UserId { get; set; }
    public virtual User User { get; set; } = null!;
    public required decimal Amount { get; set; }    // may be negative on an edited expense
}
```

`SplitRule` is a template and holds no dates:

A rule is data. What it *does* is its handler's, resolved by the rule's runtime type the
way rule versions already are -- so a `Kind` column and a switch appear nowhere, and
adding a kind is adding a class rather than editing one.

```csharp
// Nothing here says what shape a split has. "Participants with weights" is one way
// to answer, and putting it on the base would quietly rule out every rule that is
// not proportional -- "Omar pays exactly ten and the rest is even" has no weight
// that expresses it, because weights are normalised by their total and a fixed
// amount does not scale.
public abstract class SplitRule : Entity
{
    public Guid GroupId { get; set; }
    public required string Name { get; set; }
}

// The proportional family, which is where participants belong. A middle layer, not
// a leaf: what went wrong before was Shares : Percent, a leaf inheriting a leaf,
// carrying its own participants *and* the inherited ones -- the same fact in two
// units with a conversion between them, which is where the rounding bug lived.
public abstract class WeightedSplitRule : SplitRule
{
    public virtual ICollection<SplitRuleParticipant> Participants { get; } = [];
}

public sealed class EvenSplitRule    : WeightedSplitRule;   // names nobody = everyone
public sealed class PercentSplitRule : WeightedSplitRule;   // hundredths, summing to 10000
public sealed class SharesSplitRule  : WeightedSplitRule;   // whole shares

// One weight column, meaning whatever the kind means. Weight is defaulted rather
// than required because an even rule names people without weighting them.
public class SplitRuleParticipant : Entity
{
    public Guid SplitRuleId { get; set; }
    public Guid UserId { get; set; }
    public int Weight { get; set; } = 1;
}

// Behaviour, off the entity and dispatched by type.
public interface ISplitRuleHandler<in TRule> : ISplitRuleHandler where TRule : SplitRule
{
    IReadOnlyList<SplitAmount> Divide(TRule rule, decimal amount, Guid payerId,
        IReadOnlyCollection<Guid> members);
    string? Invalid(TRule rule);
}
```

Two things worth noticing about that. `PercentSplitRule` and `SharesSplitRule` divide
**identically** -- both hand their stored weights to the calculator, which normalises by
whatever total it is given. Shares needing no conversion into percentages is exactly what
removes the old `SharesRuleVersionHandler` drift correction; what is left that is
genuinely each kind's own is what makes it invalid. And an even rule that names nobody
divides between the current membership, so it keeps dividing evenly when somebody joins
instead of freezing today's members into weights; naming people narrows it instead.

public class Category : Entity
{
    public Guid GroupId { get; set; }
    public required string Name { get; set; }
    public Guid? DefaultSplitRuleId { get; set; }
    public virtual SplitRule? DefaultSplitRule { get; set; }
}
```

Mapping, in `AppDbContext`:

- `Transaction` keeps the default TPH strategy; `HasDiscriminator` is left implicit and
  the discriminator column is indexed explicitly, because EF does not and every expense
  listing filters on it.
- `Amount` stays `HasPrecision(18, 2)`; `Currency` is `HasMaxLength(3).IsFixedLength()`.
- `TransactionSplit` gets a unique index on `(TransactionId, UserId)` -- a person appears
  at most once in a split -- and a plain index on `UserId`, which is the balance query's
  access path.
- `Transaction` gets `(GroupId, Date)`, which is every listing's sort and filter.
- `Category` gets a unique index on `(GroupId, Name)`, inheriting the constraint
  `Rule` had on `(GroupId, Category)`.
- Splits cascade-delete with their transaction. A `SplitRule` referenced as a category's
  default is `Restrict`: deleting a rule that a category points at should fail loudly
  rather than silently un-defaulting the category.

## Where the split arithmetic lives

Today it is written three times: once in LINQ that has to survive translation to SQL
(`GroupService.NetBalances`), once in memory over loaded rule users
(`TransactionService.GetTransactionSplits`), and a third time as a shares-to-percent
conversion with its own drift correction (`SharesRuleVersionHandler.ToEntity`). The three
do not agree. The first two truncate each non-payer's share and give the payer the
remainder; the third rounds to two decimal places and gives the drift to
`calculated[^1]` -- whichever participant the dictionary enumerated last, which is not a
rule anybody chose and is the "random member" rounding bug.

After this phase the arithmetic exists once, in C#, and runs at write time:

```csharp
public static class SplitCalculator
{
    // Weights -> amounts summing exactly to `amount`. Every participant but the
    // payer is truncated to the cent; the payer absorbs the remainder.
    public static IReadOnlyList<(Guid UserId, decimal Amount)> Divide(
        decimal amount, Guid payerId, IReadOnlyList<(Guid UserId, int Weight)> weights);
}
```

The payer absorbs the remainder in every case -- even, shares and percent alike -- so the
result depends on nothing but the inputs. That is the deterministic-remainder fix from
Phase 0, arriving here instead, because here it is one function rather than three
patches.

The balance query then has no arithmetic left to duplicate:

```csharp
from user in group.Users
select new GroupNetBalance
{
    AmountPaid = transactions.Where(t => t.PaidByUserId == user.Id).Sum(t => t.Amount),
    AmountOwed = splits.Where(s => s.UserId == user.Id).Sum(s => s.Amount),
    // Balance = AmountPaid - AmountOwed
}
```

Both sums are over indexed columns with no joins into the rule tables, no `Math.Truncate`
in SQL, and no correlated subquery per participant. `DebtCalculationService` is untouched:
it consumes `GroupNetBalance` and never knew where the numbers came from.

## Migrating the rows

One EF migration, `ReshapeTransactionsAsExpensesAndTransfers`, with the backfill written
as C# in the migration rather than raw SQL -- the settlement pairing needs matching logic
that reads badly in SQL and is worth testing.

The migration is destructive and one-way. It is safe because the only data is seed and
development data, exactly as the roadmap assumed; a production database would need a
different plan and does not exist yet. That is a fact about today, so it goes in the
migration's own comment, where the next person meets it.

**Order.** Additive first, backfill second, destructive last, so a failure part-way leaves
a database that still reads:

1. Add `Group.Currency` (default `USD`), the `Transaction` columns
   (`GroupId` nullable, `PaidByUserId`, `Currency`, `Date`, the discriminator,
   `CategoryId`), and the `TransactionSplit`, `SplitRule`, `SplitRuleParticipant` and
   `Category` tables.
2. Backfill categories and rules: each `Rule` that is neither `PersonalDefault` nor
   `Settlement` becomes one `Category` named after `Rule.Category`, plus one `SplitRule`
   named the same, built from that rule's **latest** version's participants. The category
   points at it. Two categories with identical rules stay two rules; deduplication changes
   what a later edit affects, so it is a person's decision, not a migration's.
3. Backfill expenses: every transaction whose rule version is a `PercentRuleVersion` (which
   includes shares) becomes an `Expense` with `GroupId` set, `CategoryId` pointing at the
   category made from its rule, and split rows computed from the version's percentages by
   the same truncate-and-give-the-payer-the-remainder formula the balance query used. The
   formula is reproduced in the migration rather than called from `SplitCalculator`:
   a migration must keep computing what it computed the day it ran, and must not follow
   later edits to production code.
4. Backfill personal expenses: every transaction on a `PersonalRuleVersion` becomes an
   `Expense` with `GroupId = null`, `CategoryId = null` and a single split to the payer
   for the full amount.
5. Collapse settlements into transfers, below.
6. Drop `Transaction.RuleVersionId`, the four rule-version tables, `PercentRuleUser`,
   `SharesRuleUser`, `Rule`, `RuleFlags`, and `User.PersonalGroupId` and the personal
   `Group` rows with it.

**Collapsing a settlement pair.** A settle writes two rows sharing a group, a timestamp
and a name, with opposite amounts: `+amount` paid by the other member, `-amount` paid by
whoever pressed the button. Balance is paid minus owed and settlements owe nothing, so
the effect today is that the positive row's payer rises by `amount` and the negative
row's payer falls by it.

A transfer reproduces exactly that: `PaidBy` rises by `Amount`, and the single split makes
the recipient fall by it. So the pairing is

> **from** = the payer of the **positive** row, **to** = the payer of the **negative**
> row, **amount** = the absolute value,

matched on `(GroupId, DateTime, Abs(Amount))` with opposite signs. An unpaired row -- a
half-written settle from a crash, which the seed data does not contain but a developer
database might -- is migrated as a transfer to itself only if that preserves the balance;
otherwise the migration stops and says which row, because guessing here silently moves
somebody's money.

This preserves balances; it does not correct them. Whether the *direction* is what a user
pressing Settle intended is a separate question, filed with the Phase 0 behaviour bugs
and not answered by a migration whose correctness criterion is that nothing moves.

**The correctness criterion, and the test that enforces it.** For every user in every
group, the net balance computed after the migration equals the net balance computed
before. That single invariant covers the percentage truncation, the personal rows and the
settlement pairing at once, and it is the migration's only real test:
`TransactionReshapeMigrationTest` snapshots every `(GroupId, UserId) -> Balance` over the
seeded database, migrates, recomputes with the new query, and asserts equality to the
cent.

## Keeping JSON Patch

`PATCH` stays, so `PatchedModel.IsValid`, `GetUpdateModel` and the pinned
`Microsoft.AspNetCore.JsonPatch.SystemTextJson` reference all stay. What changes is that
the model being patched now sits in front of rows that must stay consistent with it, and
JSON Patch is the one verb that can change *part* of a model. That matters in exactly one
place, and it is the place that matters most:

> A patch that changes `Amount` and says nothing about `Splits` leaves splits that no
> longer sum to the amount -- and every balance in the group is that sum.

`PUT` would not have this problem, because a whole model always arrives. So the rule is
explicit, and it is the reason this exclusion is not free:

None of this bites yet: `Splits` is not on the update model, so an edit always recomputes
them from the rule, and there is nothing a patch can half-change. It starts mattering the
moment a per-expense split is editable, which is what Phase 2's expense dialog is for.
The shape it takes then:

- The update model is `UpdateExpenseRequest`, carrying an optional
  `IReadOnlyList<SplitInput>? Splits`.
- After the patch is applied and validated, the service recomputes splits **unless the
  patch document itself touched `/splits`**. Recomputation runs `SplitCalculator` over
  the expense's category default, or an even split when it has none -- the same path a
  create takes.
- When the patch *did* touch `/splits`, the given splits are used verbatim and must sum
  to the (possibly also patched) amount, or `422 SplitsDoNotSumToAmount`.
- Detecting "touched `/splits`" reads the operations on the `JsonPatchDocument`, before
  it is applied. That is a small amount of awkward code that `PUT` deletes outright, and
  it should be deleted when the verb changes.

Transfers are not patchable in the parts that define them. `Name`, `Description` and
`Date` may be patched; an operation touching `/amount`, `/paidByUserId` or the splits of a
`Transfer` is refused with `409 TransferNotEditable`. Correcting a transfer is deleting it
and settling again, which is one action in the UI and avoids a second way to move money.

## What is deleted

| Deleted | Why it can go |
|---|---|
| `RuleVersion`, `PersonalRuleVersion`, `PercentRuleVersion`, `SharesRuleVersion`, `SettlementRuleVersion` | Splits are on the transaction; the template is `SplitRule` |
| `PercentRuleUser`, `SharesRuleUser` | `SplitRuleParticipant`, one weight column |
| `Rule`, `RuleFlags` | `Category` is the label; `SplitRule` is the split; no rule is a pseudo-rule any more |
| `IRuleVersionHandler` and its four implementations, `RuleVersionHandler` reflection dispatch, `RuleVersionServiceExtensions` | ~~One shape of rule, converted directly~~ **Retained, retargeted** -- see below |
| `RuleFilter` | Rules are bounded per group and returned whole -- see [Listing contract](roadmap.md#listing-contract) |
| `TransactionService.GetTransactionSplits` | Splits are rows; read them |
| `RuleVersionReferencesRemovedMember`, `ErrorCodes.RuleVersionHasRemovedMember` | Replaced by `MemberHasBalance` on removal |
| `User.PersonalGroup`, the personal `Group` rows, `Rule.PersonalDefault` | Personal is `GroupId is null` |
| `ErrorCodes.RuleNoUserTransactions`, `TransactionPayerRequiresRule`, `TransactionRuleRequired`, `GroupHasNoRule` | A group with no rules can record an expense: no category, even split |

The handler row is a correction. This plan originally had the handler machinery deleted
along with the hierarchy it served, on the grounds that one shape of rule needs no
dispatch. That was wrong twice over: there is not one shape of rule, and even if there
were, deleting the pattern only moves the problem -- something still has to turn a wire
DTO into the right subtype, and without a handler resolved by type that something is a
switch, in the API layer, needing an edit every time a kind is added. The pattern is kept
and pointed at `SplitRule` instead: `ISplitRuleHandler<TRule>`, a dispatcher that makes
the generic interface from the rule's own type, and one handler per kind.

The split-rule handlers sit in `GroupSplit.API/Services/SplitRuleHandlers`, beside the
rule-version handlers they are modelled on, and register from `Extensions` beside theirs.
They started in `GroupSplit.Data` on the theory that the seeder needed them and could not
reach the API project; the seeder references `GroupSplit.API` directly, so that was simply
untrue and the split bought nothing. The arithmetic itself -- `SplitCalculator` and the
two value types -- does stay in `GroupSplit.Data`, because `ExpenseSplitting` uses it
there and the seeder does divide through that.

What differs from the rule-version handlers is lifetime: these are singletons, because
dividing needs the rule, the amount, the payer and the membership and nothing else. The
rule-version handlers are scoped because theirs genuinely query the database.

That last row is worth reading twice. Four error codes and
`RejectGroupTransactionWithoutARule` exist to handle "you picked a group but it has no
rule to record against", a state that only the current model can be in. In the new one an
expense needs a group, an amount and a payer; a category is optional and a missing one
means an even split. The failure disappears with the thing that caused it.

## What the build changed about this plan

Three things came out differently once the code was written, and this section is the
record rather than a revision -- the plan above still reads as it was decided.

**`SplitCalculator` lives in `GroupSplit.Data`, not the API.** The seeder needs the same
division. Seed data divided even slightly differently from the way the app divides gives
every developer a set of balances that no sequence of user actions could have produced,
which is a worse bug than the duplication was.

**`Expense` keeps its `RuleVersion` for now.** Steps 3 and 4 moved the *storage* -- splits
are rows, balances are sums -- while the wire contract still speaks in
`RuleVersionId`, so the client and its tests never had to change in the same commit as
the balance query. `Category` takes over in step 5, and the column goes with the rest of
the old model in step 6. The nullability falls out of putting it on the leaf: a transfer
never had one.

**The migration corrects one thing rather than preserving it.** Balance preservation was
the stated criterion, and it holds everywhere the payer is a participant. Where they are
not -- somebody paying for a split they are no part of -- the old query truncated each
participant's share and left the remaining cents belonging to nobody, so the group's
balances did not sum to zero. Preserving that faithfully would mean writing splits that
do not sum to their amount, breaking the one invariant everything else rests on. The
migration assigns those cents the way the application now does. The better test came out
of noticing it: **every group's balances sum to zero** is a stronger statement than
"unchanged", and it is the one the suite asserts.

The migration was run against PostgreSQL 16 rather than reasoned about: seeded with
thirds that do not divide, an amount that truncates on every participant, a personal
expense and a settlement pair, then compared member by member. Every balance came out
identical to the cent, five rows became four, and no transaction's splits failed to sum.

## Order of work

Each step builds and keeps the suite green. Test counts are the floor, not the target;
the coverage gate in CI is a ratchet and does not move down.

Steps 1 to 4 are done, in three commits -- the arithmetic, then the new tables, then the
cut-over. Steps 5 to 7 are not.

1. **Entities and mapping, no behaviour.** New types, `AppDbContext` configuration,
   `SplitCalculator` and its unit tests. Nothing reads them yet. The old model still
   serves every request.
2. **The migration and its balance-preservation test.** The database moves; the API does
   not compile against the new tables yet, so this step ends with both models mapped.
   This is the only step that is hard to reverse, and it is deliberately alone.
3. **The write path.** `TransactionService` creates `Expense` and `Transfer` with splits;
   `GroupService.Settle` builds a `Transfer` instead of a signed pair. Reads still work
   because the balance query is next.
4. **The read path.** `NetBalances` becomes the two sums; listings read `Set<Expense>()`;
   `TransactionDetailsResponse.Splits` reads split rows. Settlement rows stop appearing in
   expense lists here, by construction rather than by a filter -- which is the Phase 0
   item this phase absorbs.
5. **Categories and rules.** `RulesApi` becomes categories and split rules; the handler
   hierarchy and `RuleFilter` are deleted.
6. **Delete the old model.** Entities, migration for the drop, `RuleFlags`, the dead
   error codes. Nothing references them by now, so this step is mechanical and should be
   reviewed as a pure deletion.
7. **The client.** DTOs gain `$type`; the transaction dialog sends splits; the grid reads
   `Set<Expense>()`-backed listings. The two client write paths are not unified here --
   that is Phase 2's command layer.

## Tests that change

Rewritten against behaviour rather than against the rule hierarchy:

- **Splits sum to the amount**, for even, shares and percent, including amounts that do
  not divide -- 100.00 three ways, 0.01 four ways, and a percentage set that truncates on
  every participant.
- **The remainder goes to the payer**, and to the payer alone, whichever participant the
  dictionary happens to enumerate last. This is the regression test for the bug the third
  copy of the arithmetic had.
- **A transfer moves both balances and appears in no expense list** -- the discriminator
  doing the work a filter used to.
- **Patching an amount rescales the splits; patching splits alone must sum to the new
  amount.** The invariant from [Keeping JSON Patch](#keeping-json-patch), which is the
  only new failure mode this phase introduces.
- **A group with no categories records an expense**, evenly split.
- **Removing a member who is owed money is refused**; removing one who is square is not.
- **Balances are preserved across the migration**, per
  [the correctness criterion](#migrating-the-rows).

Deleted with their subjects: every test that constructs a `RuleVersion` subtype, asserts
on `RuleFlags`, or expects `RuleVersionHasRemovedMember`.
