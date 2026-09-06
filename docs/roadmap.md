# Roadmap: from expense splitting to a financial tracker with bank sync

Read at `e82cd6b` (`fix/stale-ui-after-writes`, 2026-09-05). Covers `src/`, `tests/` and
`docs/`. Also published as an artifact: [Group Split, next](https://claude.ai/code/artifact/daa694ac-9539-4aed-a7b3-eec90d9c802d).

What stands between the app as it is today and a financial tracker with expense sharing
and bank sync through Plaid: what is missing, which parts of the model are carrying more
weight than they need to, and the order to change them in.

## Where it stands

The foundation is in better shape than most projects at this stage. Sign-in is real
(Keycloak, a BFF that forwards `/api` with a refreshed bearer token, a WASM-or-Server
render mode), every error is RFC 9457 problem details with a shared code catalog, there
is one design system carried into Keycloak's own pages, and the Aspire model runs the
same stack locally and in Compose. The client is generated from the API's OpenAPI
document at build time, so a new endpoint reaches the UI as a typed method.

| | |
|---|---|
| API endpoints | 30, across groups, invitations, categories, split rules, transactions, users |
| Tests | 451 methods; ~74% line coverage on hand-written code |
| UI | 5 pages, 11 dialogs; one command layer per aggregate behind every write |
| Absent | bank data |

What works end to end today: create a group and invite people to it by email, whether or
not they have an account yet; define split rules and point categories at them; record an
expense against a category or state its shares yourself, in a group or just for yourself;
see per-member net balances, a minimised list of who pays whom and the group's whole
history; settle up from either side; leave a group or archive it; see where you stand
across every group at once; delete an account once settled. The seeder fills two demo
groups.

The gap left is bank data. The domain was shaped around one question -- *how is this
category split?* -- and a tracker with imported bank transactions asks different ones:
*whose money was this, which group does it belong to, and how should this one expense be
shared?* Phases 1 and 2 answered those; phase 3 brings the transactions in.

## What is missing

Grouped by what it blocks. **Plaid** marks items the integration depends on, not merely
benefits from.

### Domain

| Gap | Today | Needed | Priority |
|---|---|---|---|
| ~~Per-expense split~~ **Done** | A split was a `Rule`; every transaction pointed at a rule version. "Split this dinner between three of the five of us" meant creating a rule first. | Each transaction carries its own split, stored as rows. Rules are templates a category may point at, pre-filling the division; an expense may state its own instead, and the dialogs offer both. | Done |
| ~~Category separate from split~~ **Done** | `Rule.Category` was both the label and the split. | `Category` is a label that may point at a `SplitRule`; the split is chosen per expense. See [Category and split](#category-and-split). | Done |
| Currency | `decimal(18,2)` with no currency; UI hard-codes `$`. | `Currency` on the transaction, a default on the group, Plaid's `iso_currency_code` mapped straight in. | High, Plaid |
| ~~Group on the transaction~~ **Done** | Group was reached through `RuleVersion -> Rule -> Group`. | `GroupId` on the transaction, nullable, and null means personal -- written as null since phase 2 rather than pointing at a hidden group. | Done |
| External identity and source | Nothing distinguishes a typed expense from an imported one. | A provider-neutral `BankTransaction` staging table holding the imported row (external id, merchant, pending state, account) and a nullable link from `Transaction` to it. The provider stays behind an interface. | High, Plaid |
| ~~Settlement as its own thing~~ **Done** | Two mirrored `Transaction` rows (`+A` and `-A`) under a system rule, showing up as expenses everywhere. | A `Transfer` leaf: one row, one split to the recipient. Either member can record one, and the group's Activity tab is where they show. See [Settlements as transfers](#settlements-as-transfers). | Done |
| ~~Pagination and server filtering~~ **Done for expenses** | Every list was fetched whole, and the grid searched the rows it had been handed. | Offset paging, sorting and filtering on both expense listings, plus a summary endpoint for the figures beside a page. See [Listing contract](#listing-contract). The remaining lists are bounded by group size. | Done |
| ~~Membership as a record~~ **Done** | Implicit many-to-many; `AddGroupMembers` silently dropped any email that was not already an account. | `GroupMembership` carries `ArchivedAt` and `JoinedAt`; invitations are a `GroupInvitation` table, since an invited address has no user id to key a membership on. Invite, withdraw, accept, decline and leave are all endpoints. | Done |
| Your share, not just what you paid | `GET /transactions` returns rows where `User == you`. `GET /users/me/position` now totals the position across groups and the home page leads with it. What is still missing is the listing: what you *owe* on other people's expenses, row by row. | A "your share" listing over `TransactionSplit`, beside the one that lists what you paid. | Medium |
| Recurring, budgets, receipts | Absent. | Later. Budgets by category become natural once categories are labels. | Later |

### Behaviour that reads as bugs today

- ~~**Settlements count as expenses.**~~ **Fixed in phase 1.** A settlement is a
  `Transfer`, and every expense surface reads `Set<Expense>()`, so they are not in the
  lists or the totals rather than filtered out of them. Phase 2 gave them a listing that
  *does* show them -- the group's Activity tab -- because a balance moving with nothing to
  explain it is its own kind of wrong.
- ~~**The personal group is a group.**~~ **Fixed in phase 2.** A personal expense has
  `GroupId is null`, the hidden group is deleted, and personal is a filter on the expenses
  page. Nothing needs hiding because there is nothing there.
- ~~**Only the creditor can settle.**~~ **Fixed in phase 2.** `SettleRequest.Direction`
  says which way the money went, and the group page offers "They paid" under *owed to you*
  and "I paid" under *you owe*. Stated rather than read off the balance, which would get
  the debtor's case exactly backwards.
- ~~**Adding a member silently drops an unknown email.**~~ **Fixed in phase 2.** Inviting
  is `POST /groups/{id}/invitations`, and an address with no account behind it is a
  standing invitation that is waiting when they sign up.
- **Even split is non-deterministic.** `RuleEditorForm.SplitPercentEvenly` and
  `ConvertSharesToPercentages` hand the rounding remainder to a random member. Only the
  rule editor's pre-fill is affected -- the division that is actually stored has been one
  function since phase 1 -- so this is now a cosmetic wobble in a form rather than money
  going to the wrong person.
- **Mixed clocks.** `DateTime.Now` in `RuleEditorForm`; `UtcNow` everywhere the server
  writes a time. `Settle` and `DetachMember` are on `UtcNow` as of phase 1.
- ~~**Re-adding a member.**~~ **Fixed in phase 2.** Joining goes through an invitation,
  which skips an address already in the group; `Accept` is a no-op for somebody already a
  member.

## What is too complex

Each of these was a reasonable answer to a real problem. Together they mean a new feature
has to be threaded through four tables, three handler interfaces and two mirrored
formulas before it reaches the screen.

### The rule-version hierarchy

`Rule -> RuleVersion` (TPT, four tables) `-> Personal | Percent | Shares | Settlement`,
where `Shares` derives from `Percent` and writes both `RuleUsers` and `SharedRuleUsers`.
Editing a rule closes the version and opens a new one so old transactions keep their
split.

- **Why:** transactions reference the split, so the split must be immutable. Versioning
  gives that.
- **Cost:** `IRuleVersionHandler<,>` x3 with a reflection dispatcher, `Equals` to decide
  whether an edit is "really" a change, `DetachMember` needing two queries because the
  subtype hides its members, `RuleVersionHasRemovedMember` checks on every write.
- **Instead:** store the split *on the transaction*. Then the rule is a plain, editable
  template and history is frozen by construction. The hierarchy, the handlers and the
  version tables go.

### Balances computed live in SQL, twice

`GroupService.NetBalances` walks every transaction x every rule member and applies
`Truncate(amount * pct) / 100` with the payer absorbing the remainder.
`TransactionService.GetTransactionSplits` repeats the same formula in C# for the detail
dialog.

- **Cost:** O(transactions x members^2) per balance read; two copies of the money
  arithmetic that must agree to the cent; unreadable EF translation.
- **Instead:** a `TransactionSplit(TransactionId, UserId, Amount)` row written once.
  Balance is `SUM(paid) - SUM(split)` over the whole ledger: one `GROUP BY`, indexable, and the
  detail dialog just reads rows.

### Settlement and Personal as pseudo-rules

A settlement is a system `Rule` with a `SettlementRuleVersion` per counterparty and two
`Transaction` rows of opposite sign. The personal ledger is a hidden `Group` with a locked
`"Default"` rule. `RuleFlags`, `RuleFilter(IsSystem, AllowUserTransactions)` and
`RejectGroupTransactionWithoutARule` exist to keep these from leaking -- and they still
leak into lists and totals.

- **Instead:** a settlement is a `Transfer`, a sibling of `Expense` under `Transaction`,
  with one split to the recipient -- the same arithmetic as the "Daniel pays 100%" rule
  people have already invented as a workaround, without the rule. A personal transaction
  is one with `GroupId = null` and no splits. `RuleFlags`, `RuleFilter`, the settlement
  rule and the personal group all disappear.

### Update paths written as one query

`TransactionService.Update` validates payer, rule version, group membership and flags in
a single LINQ expression with three `DefaultIfEmpty` joins. Correct, and nobody will want
to touch it.

- **Instead:** load the transaction, load the group with members, check each rule in
  plain C#. With splits on the transaction the checks shrink to "payer and participants
  are members".

### JSON Patch for every update

Three `PATCH` endpoints take `JsonPatchDocument<T>`, apply it to a re-read model,
re-validate through `PatchedModel`, and the client diffs by hand
(`EditRuleDialog.VersionHasChanged`). The client's patch package is pinned to a preview
build because every release breaks the WASM restore.

- **Instead:** `PUT` with the full request record. The dialogs already hold the full
  model; the API already re-validates it. Drops a dependency, a helper, and a class of
  subtle bugs.

### Two write paths in the client

Pages write through `*PageStateService`, which announces through `DataChangeNotifier`.
`ManageRulesDialog` and `TransactionDetailsDialog` call the generated clients directly
and announce themselves -- or, for rules, don't.

- **Instead:** one thin command layer per aggregate that every dialog uses; the notifier
  stays. Small, but it is the pattern the Plaid review inbox will copy, so fix it before
  copying it.

## The target model

Fewer tables, and each one answers a question a person would ask.

| Today | Proposed |
|---|---|
| `Group` <-> `User` (implicit join) | `Group` (+ `Currency`, `ArchivedAt`) |
| `Rule` + `RuleFlags` | `GroupMembership` (Role, Status, JoinedAt, LeftAt, InvitedEmail) |
| `RuleVersion` TPT: Personal, Percent, Shares (: Percent), Settlement | `SplitRule` (GroupId, Name, Kind, participants + weights) -- an editable template |
| `PercentRuleUser`, `SharesRuleUser` | `Category` (GroupId, Name, DefaultSplitRuleId?) -- a label that may pre-fill a split |
| `Transaction -> RuleVersion` | `Transaction` (abstract, TPH: GroupId?, PaidBy, Amount, Currency, Date, Name, Description, BankTransactionId?) with leaves `Expense` (+ CategoryId?) and `Transfer` |
| Personal = hidden group; settlement = +/- transaction pair | `TransactionSplit` (TransactionId, UserId, Amount) |
| | `BankConnection`, `LinkedAccount`, `BankTransaction` -- import side, provider-neutral; see [Bank data without a provider in the model](#bank-data-without-a-provider-in-the-model) |

```csharp
// One table, one level. Splits are written once, at create or edit, and sum
// to the amount; remainder to the payer, as today.
public abstract class Transaction : Entity
{
    public Guid? GroupId { get; init; }            // null = personal
    public required Guid PaidByUserId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }  // ISO 4217
    public required DateTimeOffset Date { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    // The only trace of where it came from. Null for a typed entry; otherwise
    // the imported row it was filed from. Nothing provider-specific lives here.
    public Guid? BankTransactionId { get; init; }

    public ICollection<TransactionSplit> Splits { get; } = [];
}

// Something the group spent. Category only makes sense here.
public sealed class Expense : Transaction
{
    public Guid? CategoryId { get; init; }
}

// Money moving between two members. Exactly one split, to the recipient,
// for the full amount -- the constructor is the only way to make one.
public sealed class Transfer : Transaction
{
    public Transfer(Guid groupId, Guid from, Guid to, decimal amount, string currency,
        DateTimeOffset date)
    {
        // GroupId = groupId, PaidByUserId = from, ...; Splits.Add(new(to, amount))
    }
}

// Balance for a group, one query over Set<Transaction>():
//   paid(u) = Σ Amount        where PaidByUserId = u
//   owed(u) = Σ Split.Amount  where Split.UserId = u
//   net(u)  = paid − owed
//
// A transfer from Loraine to Daniel for 3.00: Loraine paid +3, Daniel owed +3.
// Loraine's net rises, Daniel's falls. No settlement term, no second row.
```

### Settlements as transfers

The screenshot of the Home group makes the case. Members had already worked around the
Settle button by creating a "Daniel pays" rule -- one participant, 100% -- and recording
a settlement as an ordinary expense against it. The arithmetic is right; only the
modelling was missing. A `Transfer` is that idea made first-class: one row, the payer is
the person paying back, the single split names who they paid. It goes through the same
splits, the same balance query, the same edit and delete paths, and an imported bank row
that is a Venmo or Zelle payment to a member can be filed as a transfer from the review
inbox -- something a separate settlement table could not do without duplicating the
import link.

What it costs is one discipline: every surface that means *expenses* -- lists, totals,
category breakdowns, the "You paid" tiles, the grid -- reads `Set<Expense>()`, and only
the balance query and the activity feed read `Set<Transaction>()`. EF adds the
discriminator predicate to `Set<Expense>()` itself, so the filter cannot be forgotten
the way an extension method can.

#### Why TPH, given what TPT did to rule versions

Inheritance earns its keep when the leaves carry different data, and here they do, if
only slightly: `CategoryId` belongs to an expense and has no meaning on a transfer. TPH
puts it on `Expense` alone instead of a nullable column with a "must be null when it is a
transfer" check, and the `Transfer` constructor makes the one-split invariant
unbreakable. It also matches the wire contract the client already understands:
`ExpenseResponse` and `TransferResponse` under `TransactionResponse` with a `$type`
discriminator, as the rule-version DTOs do today.

What went wrong with `RuleVersion` was TPT -- four tables, a join per read -- and a
three-level chain (`Shares : Percent : RuleVersion`) with duplicated collections. TPH is
one table and EF's default. The guardrails:

- **One level.** `abstract Transaction`, `sealed Expense`, `sealed Transfer`. No leaf
  inherits from a leaf.
- **Shared columns on the base.** Leaf-only columns are nullable in the table by
  construction; that is expected, not a smell.
- **No `Kind` property alongside.** The discriminator is the discriminator; branch with
  `is Transfer` or `Set<Transfer>()`.
- **Index the discriminator.** EF does not by default, and every expense query filters
  on it.
- **A third kind is a third leaf.** `Income`, for a Plaid deposit in personal tracking,
  is a sealed class and a DTO -- the same cost as an enum value would have been.

### Category and split

Today the rule *is* the category: `Rule.Category = "Groceries"` and its versions hold
the split. One thing doing two jobs, which is why the seed data carries Groceries and
Utilities as two identical shares rules, and why a category with no rule cannot record
anything.

In the target model the relationship stays but points the other way. A category is a
label the group owns, and it may name a default split rule:

```
Group "Home"
 ├─ SplitRule  "Household 3-way"  { Daniel 100000, Anabel 15000, Loraine 100000, Omar 100000 }
 ├─ SplitRule  "Even"             { equal among all members }
 ├─ Category   "Groceries"   -> default: Household 3-way
 ├─ Category   "Utilities"   -> default: Household 3-way
 └─ Category   "Dining out"  -> default: none (falls back to Even)

Transaction "Costco" 220.50, Category = Groceries
 └─ Splits: Daniel 70.00, Anabel 10.50, Loraine 70.00, Omar 70.00   <- copied at write time
```

Recording a Groceries expense pre-fills the split from Household 3-way and stores the
result on the transaction. What that buys:

- **Fixed by default, adjustable when it matters.** Every Groceries expense gets the
  household split without anyone touching it, exactly as today -- but "don't charge Omar
  for his own birthday cake" is an edit to one expense, not a rule change.
- **One rule, many categories.** Groceries, Utilities and Cleaning point at the same
  rule; when a roommate moves out it changes in one place.
- **Editing a rule never rewrites history.** Existing transactions already hold their
  splits, so the rule needs no versions.
- **Imports file themselves.** A Plaid row categorised Groceries and assigned to Home
  gets Household 3-way applied automatically; the review inbox only needs a person for
  rows whose category has no default.

A category with no default rule uses an even split among current members. A `Locked`
flag on the category -- "this split may not be overridden per expense" -- is a later
addition if a group asks for it; a default covers nearly every case.

### Bank data without a provider in the model

The ledger -- `Transaction` and its leaves, `TransactionSplit` -- knows nothing about
banks. Imported data lives on its own side of a seam, and the seam is provider-neutral:
every aggregator (Plaid, Teller, TrueLayer, GoCardless) and a CSV or OFX file all yield
the same shape, an account with rows that have an external id, a date, an amount, a
merchant and possibly a pending state.

```
BankConnection   (UserId, Provider, ProviderItemId, AccessTokenCiphertext, Cursor,
                  Status, LastSyncedAt)               -- one per linked institution
LinkedAccount    (BankConnectionId, ProviderAccountId, Name, Mask, Type, Currency)
BankTransaction  (LinkedAccountId, ProviderTransactionId, Date, Amount, Currency,
                  MerchantName, Description, ProviderCategory, Pending,
                  ReplacesId?, Status: New | Filed | Ignored, RawJson)
                  unique (LinkedAccountId, ProviderTransactionId)

IBankConnector   CreateLinkSession · Exchange · Sync(cursor) · VerifyWebhook
                  -> PlaidConnector today; a second provider is a second class
```

Rules of the seam:

- **A pending row is not a transaction.** It stays a `BankTransaction` with
  `Pending = true` until the provider posts it (`ReplacesId` points the posted row at the
  pending one) or the person files it anyway. `Transaction` has no pending state.
- **Filing copies, then links.** "Add to group" creates a `Transaction` from the row's
  date, amount, currency and merchant, applies the category's default split, and sets
  `BankTransactionId`. Editing the transaction afterwards never touches the imported row;
  re-syncing never touches the transaction.
- **Provider fields stay in `RawJson` and `ProviderCategory`.** The app reads a small
  normalised set; anything Plaid-specific it needs later is in the raw payload, not in a
  column.
- **The webhook route is `/webhooks/{provider}`**, and each connector verifies its own
  signature scheme.

Migrating away from Plaid, or adding a second provider, is then a new `IBankConnector`
and a value in `Provider`; the ledger and the UI do not change.

**Migration.** One EF migration adds the new tables; a data step runs today's
`GetTransactionSplits` once per existing transaction to materialise splits, creates one
`Category` per existing `Rule` with the rule's current version as its default
`SplitRule` (identical versions collapse into one rule), converts each
`SettlementRuleVersion` pair (`+A` paid by the debtor, `-A` paid by the creditor) into
one `Transfer` from the debtor to the creditor, re-parents personal-group
transactions to `GroupId = null`, then drops the version tables. The seeder's
`rules.json` becomes `split-rules.json` plus `categories.json`. The debt-minimisation
service is untouched -- it already takes net balances and nothing else.

## Plaid integration

Products: `transactions` only, through `/transactions/sync` with a per-item cursor.
Nothing here moves money. Sandbox in run mode; production credentials as Aspire
parameters in publish mode, next to the SMTP ones.

| Step | Who | What |
|---|---|---|
| Create link token | API | `POST /plaid/link-token` -> `/link/token/create` with `client_user_id`, webhook URL, redirect URI. |
| Run Link | Browser | Plaid's `link-initialize.js` via JS interop. A person picks a bank and signs in. Returns a `public_token`. |
| Exchange | API | `POST /plaid/items` -> `/item/public_token/exchange`. Store `PlaidItem` with the access token encrypted; fetch accounts. |
| Sync | API | `/transactions/sync` until `has_more` is false; upsert added and modified, delete removed, save the cursor. |
| Webhook | Plaid | `SYNC_UPDATES_AVAILABLE` -> queue a sync. `ITEM_LOGIN_REQUIRED` -> mark the item; Link in update mode from the UI. |
| Review | Person | Imported rows land in an inbox: assign to a group and split, keep personal, or ignore. Auto-file rules for known merchants. |

### What has to be built

- **Data.** The `BankConnection`, `LinkedAccount` and `BankTransaction` tables above;
  `PlaidConnector : IBankConnector` is the only code that speaks Plaid's vocabulary.
  Pending -> posted arrives as a new row whose `pending_transaction_id` sets
  `ReplacesId`; a `removed` entry deletes the staging row if it is still `New` and
  flags it if it was already filed.
- **Secrets.** ASP.NET Data Protection with the key ring in Postgres for the access
  tokens; never returned by any endpoint. `plaid-client-id`, `plaid-secret`,
  `plaid-env` as parameters.
- **The webhook route.** The BFF's `/api` forwarder is `RequireAuthorization()`, so
  Plaid cannot reach it. Add an anonymous forwarder for `/webhooks/{provider}`; the API
  hands the request to that provider's connector, which for Plaid verifies the
  `Plaid-Verification` JWT against `/webhook_verification_key/get`. Reject anything
  else.
- **Sync as a background job.** A hosted service with a channel; the webhook enqueues, a
  nightly sweep enqueues every item, and "Sync now" in the UI enqueues one. Per-item lock
  so two syncs cannot race on the cursor.
- **Sign conventions.** Plaid amounts are positive for money out. Map to the app's
  positive-expense convention at import and keep it in one place.
- **Categories.** Map `personal_finance_category.primary` to the group's categories at
  import; the person can override. Once a row has a group and a category with a default
  split rule, its splits are written without review. This is what makes
  category-as-label a prerequisite.
- **Client.** The generated client picks up the new endpoints. Link itself is the one
  piece of JS interop in the app; in the MAUI WebView use Plaid's Hosted Link with the
  redirect back into the app, and treat native Link SDKs as a later step.
- **Tests.** A fake `IBankConnector` for everything above the seam (sync into staging,
  dedup on re-sync, pending -> posted replacement, filing into a group applies the
  category's default split, ignored rows stay ignored across syncs); `PlaidConnector`
  tested on its own against recorded payloads (cursor pagination, `removed`, webhook
  signature accept and reject); a connection in `ITEM_LOGIN_REQUIRED` surfacing on the
  accounts page.

> **Order matters.** Importing bank transactions into today's model means every imported
> row needs a rule version to point at, and every one of them lands as a group expense or
> a personal one with no way to change its split later. Build Plaid on the target model,
> not before it.

## UX enhancements

Ordered by how often a person meets them.

### Adding an expense

- One dialog, two steps: amount, description, date and group first; then **Split** --
  *equally*, *by a saved rule*, or *custom* -- with a live per-person preview computed
  client-side from the same remainder rule the API applies.
- Drop the time picker. An expense has a date; the time is noise in a form people fill
  ten times a week.
- A group with no saved rule still works: equal split is the default, and the dialog
  offers to save the custom split as a rule.
- Category is a select over the group's categories with "add new" inline, never a gate.
  Choosing one pre-fills the split step from its default rule; the person can still
  change the split for this expense.

### The group page

- Give each group a route, `/groups/{id}`, so a link to a group can be shared and the
  browser's back button means something. Tabs: Overview, Expenses (paged, filterable),
  Members, Settle up.
- Settle from both sides: "Record a payment" available to the debtor as well; it
  writes one `Transfer`, shown in an *Activity* list as "Loraine paid Daniel $3.00",
  never in Expenses.
- Members tab shows invited-but-not-joined members; adding an email that has no account
  sends an invitation instead of silently doing nothing.
- Leave group, archive group, and a clear message when a balance blocks either.

### Home and Expenses

- Home leads with your net position across groups -- "You are owed 214.20 · you owe
  80.00" -- which needs one new endpoint. The paid-total tiles move down.
- Expenses gets three views: *Paid by you*, *Your share*, *Everything*; month grouping;
  a category breakdown for the tracker use.
- Personal stops being a group chip and becomes a "Personal" filter on Expenses and a
  tile on Home.
- The Plaid review inbox lives at `/inbox` with a badge in the nav: each row shows
  merchant, account, amount, suggested category and a one-click "keep personal / add to
  group / ignore"; bulk select for the obvious ones.
- Linked accounts page under Account: institution, last sync, a "needs attention" state
  for items that need re-login.

### Small things worth doing now

- Deterministic remainder in even split (largest share, or the payer).
- Snackbar copy says what happened to what: "Groceries added to Home", not "Transaction
  created successfully".
- Money formatting through one component that knows the currency; the `$` adornments go.

## Roadmap

Phases are sequential because each one makes the next one smaller. Sizes assume one
person, full time, and are estimates.

### Phase 0 -- Stop the bleeding (~1 week)

- ~~Exclude settlement rows from `GET /transactions`, group expense lists and every total.~~
  ~~Hide the personal group from the switcher and Home; deterministic even split.~~
  **Not done here; absorbed by Phase 1.** Each of these is a patch over a modelling
  mistake that Phase 1 removes, so doing it now is work thrown away in three weeks.
  Settlements stop appearing in expense lists once `Transfer` is its own leaf and those
  lists read `Set<Expense>()`; the personal group stops needing hiding once personal is
  `GroupId is null` and the rows are gone; the even split becomes deterministic once the
  arithmetic is one function instead of three. See
  [Phase 1: reshaping the model](phase-1-model-reshape.md).
- **Still outstanding, and not absorbed by anything: UtcNow everywhere, and guard
  re-adding a member.** These two survive the reshape untouched -- they are ordinary bugs
  rather than consequences of the model -- so striking the bullet above does not strike
  them. `DateTime.Now` is still read in `GroupService` (the settle timestamp and the
  rule-version close-out), which stores a local time in a `DateTimeOffset` column and
  makes a settlement's ordering depend on the server's zone.
- ~~Remove `IsArchive` or wire it -- pick one.~~ **Done:** wired, and personal.
  Archiving a group hides it from your own list the way archiving a note does -- it is
  `GroupMembership.ArchivedAt`, not a property of the group, so nothing about the group
  changes and no other member is affected. `POST`/`DELETE /groups/{id}/archive` write the
  caller's own membership row. Closing a group *for everyone* is a different feature and
  is not built.
- ~~Paging on the three list endpoints and the grid; send `TransactionFilter` from the
  UI.~~ **Done, for the two listings that grow without bound:** `GET /transactions` and
  `GET /groups/{id}/transactions` answer with a page, and the filter grew the fields the
  grid needs -- group, payer, category and free text -- so searching and sorting are the
  server's. Groups, rules and members stay whole; they are bounded by group size and feed
  chips and selects rather than a grid. See [Listing contract](#listing-contract).

The rest ships as a normal fix PR; no schema change beyond the archive column.

### Phase 1 -- Reshape the model (~3 weeks)

- `Transaction` as a TPH base with `Expense` and `Transfer` leaves; `TransactionSplit`,
  `GroupMembership`, nullable `GroupId`, `Currency`; `SplitRule` as a template;
  `Category` with an optional default rule.
- Data migration from rule versions; drop the TPT hierarchy, handlers, `RuleFlags`,
  `RuleFilter`.
- Balance query rewritten as sums; `DebtCalculationService` unchanged.
- ~~`PATCH` -> `PUT`; drop the pinned JSON Patch package.~~ **Deferred.** JSON Patch
  stays for now. It is the one verb that can change part of a model, so it lands on the
  invariant the balances rest on: a patch that changes an amount without mentioning the
  splits leaves splits that no longer sum to it. Phase 1 handles that explicitly rather
  than pretending it does not exist -- see
  [Keeping JSON Patch](phase-1-model-reshape.md#keeping-json-patch) -- and the awkward
  code it needs is deleted outright when the verb does change.
- Rewrite the affected tests against behaviour (splits sum to amount, remainder to
  payer, a transfer moves both balances and appears in no expense list, leaving blocked
  by balance). Coverage floor stays.

One breaking schema change, done once, while the only data is seed data. The build order,
the row-by-row migration and the decisions the roadmap left open are in
[Phase 1: reshaping the model](phase-1-model-reshape.md).

### Phase 2 -- The product surface (~2 weeks)

**Done.**

- ~~Two-step expense dialog with custom split and preview.~~ **Done.** Step one is what the
  expense is; step two is how it is carried, and it opens on the division that is actually
  going to be stored. The preview is `POST /transactions/preview`, which runs the same
  splitter the save runs -- the dialog used to divide evenly itself, and a preview that
  disagrees with the save by a cent is worse than no preview.
- ~~`/groups/{id}` with tabs; settle from both sides; activity list.~~ **Done.** A group has
  its own page with Overview, Expenses, Activity and Members, and the section is in the URL
  so a group's members page is somewhere you can be sent to. `/groups` is the list it opens
  from; the switcher is gone. Activity is `GET /groups/{id}/activity`, the one listing that
  shows transfers.
- ~~Invitations and pending members; leave and archive.~~ **Done.** `GroupInvitation` is a
  table of its own rather than a status on `GroupMembership`: a membership is keyed on
  (group, user), and the whole point is the address with no account behind it yet. Leaving
  is `DELETE /groups/{id}/members/me`, blocked by an unsettled balance like being removed
  is, and refused outright for the last member -- archiving is what they want. What the
  leaver paid stays in their own listing and totals -- leaving a flat share does not erase
  a year of your own spending -- but it can no longer be changed, since a change moves
  balances for people whose group they have left. Archiving
  landed in phase 0.
- ~~Home net position; Expenses views; personal as a filter.~~ **Done.**
  `GET /users/me/position` answers the question the app could not: where somebody stands
  across every group. It keeps the two directions apart rather than netting them, because
  owed 40 in one group and owing 25 in another is not "owed 15" to anybody. Personal is a
  filter on the expenses page, and the hidden group behind it is deleted.
- **Moving an expense between the personal ledger and a group** (and between groups) is
  an ordinary edit: `UpdateTransactionRequest.GroupId`. The shares are re-derived among the
  destination's members, the currency follows, a category from elsewhere is refused, and
  only your own expense can be made personal. This is the primitive the phase 3 review
  inbox files imported rows with, built now so that inbox is a UI over an edit that
  already exists.
- ~~One command layer per aggregate for dialogs.~~ **Done.** `IGroupCommands` and
  `ITransactionCommands` own the call, the message and the announcement; page states are
  readers that delegate their writes, and dialogs use the same commands rather than the
  generated clients. Categories and split rules still write through their dialogs -- see
  the issues on the repo.

The app is a complete expense-sharing product without bank data.

### Phase 3 -- Plaid (~3 weeks)

- `BankConnection`, `LinkedAccount`, `BankTransaction`; `IBankConnector` with
  `PlaidConnector` behind it; Link token, exchange, encrypted storage; Aspire
  parameters; sandbox in run mode.
- Sync job into staging, cursor handling, dedup, pending -> posted; `/webhooks/{provider}`
  and verification.
- Review inbox, category mapping, auto-file by category default; linked accounts page;
  re-login flow.
- Fake connector and the test list above.

Bank transactions arrive, are reviewed, and become shared or personal expenses.

### Phase 4 -- Tracker depth (open)

- Budgets per category; monthly reports; recurring detection from imported data.
- Notifications (someone added an expense, you were settled with) by mail through the
  relay that already exists.
- MAUI: Hosted Link, then native Link SDKs if the WebView flow is rough.

## Listing contract

How a listing that pages is asked and answered, so a second one does not invent its own
shape. `PageRequest`, `SortRequest` and `PagedResponse<T>` live in `GroupSplit.Shared`;
the mechanism is `IQueryable<T>.ApplySort(sort, map).ToPageAsync(page, ct)` and a
`SortMap<T>` beside the endpoint that owns it.

| Parameter | Default | Notes |
| --- | --- | --- |
| `Page` | 1 | Below 1 is clamped to 1. |
| `PageSize` | 25 | Clamped to 200. The response says which size was applied. |
| `SortBy` | the listing's default key | Case-insensitive. A key the listing does not offer is a 400 naming the ones it does. |
| `SortDescending` | the key's own default | Dates and amounts read largest first. |

The response is `{ items, page, pageSize, totalCount }`. Out-of-range values are clamped
rather than refused, because `[Range]` on an `[AsParameters]` record only runs inside the
API's own assembly -- .NET 10 validation is a source-generated interceptor on the
`AddValidation()` call site -- and one behaviour everywhere beats the stricter of two.

Every map declares a default key and a tiebreak. A page of an unordered query is not a
page: rows the chosen key cannot separate have to keep an order between requests, or
paging shows one row twice and another never.

**Expenses** sort by `dateTime` (the default, newest first), `amount`, `name`, `category`,
`group` or `paidBy`, and filter by `From`, `To`, `GroupId`, `PaidByUserId`, `Category`
(exact, case-insensitive) and `Search` (anywhere in the name, description, category, group
name or payer's name). `GET /transactions/summary` and
`GET /groups/{id}/transactions/summary` take the same filter and answer
`{ count, total }` -- what the whole match comes to, which is what the figures beside a
page have to say and what a page cannot work out for itself.

**One thing to know when adding a paged endpoint.** The clients are generated with
`/GenerateDtoTypes:false`, so a schema id is written into them verbatim as a C# type name,
and the templates only rewrite a generic name back into a generic for parameters. A new
`PagedResponse<TSomething>` therefore needs a one-line record named
`PagedResponseOfTSomething` in `GroupSplit.Shared` beside the existing one, or the app
stops compiling. The schema id is pinned in `OpenApiOptionsExtensions` so the two cannot
drift.

## Decisions to make

- **Keep rule history or not?** The plan drops rule versioning because splits live on
  the transaction. If you want "what was the Groceries rule in March", keep a lightweight
  `SplitRuleRevision` log -- but no transaction should point at it.
- **One currency per group, or per transaction?** Per transaction is what Plaid gives
  you; per group is what balances need. Proposal: both, with conversion out of scope and
  a group refusing an expense in another currency until it is in scope.
- **Who can link a bank?** An item belongs to a person, not a group. Imported
  transactions are theirs until they share one. That keeps other members from ever
  seeing an account they do not own.
- **Invitations before Plaid, or after?** They are in Phase 2 because a shared expense
  with an unknown email is the first thing a new group hits. They can slip to Phase 4 if
  bank sync is the priority.
