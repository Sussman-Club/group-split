# Bank sync

Somebody links their bank once, and from then on its card payments arrive in an inbox in the
app. Each one is added to a group, kept personal, attached to an expense somebody already
typed in, or ignored. Nothing imported becomes an expense on its own: an imported row is
staging data until somebody files it, and filing copies the bank's figures into an ordinary
expense and links the two. The ledger never learns what a provider is. The one trace of where
an expense came from is `Transaction.BankTransactionId`.

Plaid is the only provider. Bank sync is available in a deployment exactly when a connector is
registered, and the Plaid connector is registered when the API has Plaid credentials; see
[Turning it on](#turning-it-on).

## The pieces

### The connector seam

[`IBankConnector`](../src/GroupSplit.API/Services/Banking/IBankConnector.cs) is everything the
app asks of a provider, in the app's terms: a session for the provider's own linking UI,
exchanging what that UI hands back for an access token, the account list, one page of changes
since a cursor, removing an item, and verifying a webhook and saying what it means. Above the
seam (the sync engine, the inbox, the endpoints, the clients) nothing knows a provider's
vocabulary.

[`PlaidConnector`](../src/GroupSplit.API/Services/Banking/Plaid/PlaidConnector.cs) is the only
class that speaks Plaid. It does so through Going.Plaid, a community client generated from
Plaid's OpenAPI spec, because Plaid ships no .NET library of its own. Depending on one
maintainer is acceptable because nothing else references the library: replacing it touches
this file and its tests.

Connectors are keyed services, registered under their `Provider` (`"plaid"`). The same string
is stored on `BankConnection.Provider` and is the segment of the webhook route, so a second
provider is a class and a registration. For the same reason the routes are `/bank-connections`
and `/inbox` and never `/plaid/...`: a route named after the first provider would change when
the second arrived.

Two rules are fixed at the seam and enforced nowhere else. Amounts are positive for money
leaving the account; Plaid's convention already agrees with an expense's, so the connector
carries the number across untouched, and a negative amount is a credit. And
[`BankSyncException`](../src/GroupSplit.API/Services/Banking/BankSyncException.cs) is the only
exception a connector may throw, with a `Kind`:

- `LoginRequired`: the token no longer works and the person has to sign in at the bank again.
  The connector maps every Plaid item error that only the person can fix (a locked account, a
  password reset, an agreement waiting on the bank's site) to this, not only
  `ITEM_LOGIN_REQUIRED`.
- `RestartFromCursor`: the provider changed the data under a page run
  (`TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION`).
- `Transient`: anything else Plaid answers, including codes the connector has never been
  taught. A code Plaid added last week should not stop somebody's bank syncing, and a failed
  run costs nothing because it did not move the cursor.

Anything else a connector meets is a bug and propagates.

Tests above the seam script pages through
[`FakeBankConnector`](../tests/GroupSplit.API.Test/Banking/FakeBankConnector.cs); the Plaid
connector's own tests answer from recorded sandbox payloads under
`tests/GroupSplit.API.Test/Banking/Plaid/Payloads`. See [Testing](testing.md).

### What is stored

The entities are in [GroupSplit.Data/Entities](../src/GroupSplit.Data/Entities):

- `BankConnection`: one Plaid item, owned by one person. The access token, encrypted; the sync
  `Cursor`; `Status` (`Active`, `LoginRequired`, `Revoked`); and the flags the linked-banks card
  reads, `AccountsNotShared` and `SignInExpiring`, beside `AccountsRekeyed`, which only a
  re-link sets. Unique on `(Provider, ProviderItemId)`. Deleting a user account cascades to its
  connections: an item belongs to a person and there is nobody else it could belong to.
- `LinkedAccount`: an account under a connection, with the name, mask, type and currency the
  provider reports. `AccessRevoked` marks one the person withdrew. Accounts are never deleted
  while their connection exists, because rows point at them.
- `BankTransaction`: one imported row. Unique on `(LinkedAccountId, ProviderTransactionId)`,
  the key every upsert relies on. `Status` is `New`, `Filed`, `Ignored` or `Superseded`;
  `ReplacesId` points a posted row at the pending row it settled; `RemovedAt` stamps a filed
  row the bank withdrew; `RawJson` keeps the provider's row verbatim (`jsonb` in Postgres).
- `Merchant`: one row per place, unique on the normalised name, shared by every row and
  expense that paid there. The sync resolves the provider's merchant name to one and filing
  copies the link onto the expense, so a logo the provider supplies next month appears on
  every expense at that place. Nobody can rename, re-logo or delete a merchant
  (`MERCHANT_EDIT_FORBIDDEN`), since that would change what every group that spent there sees,
  and a caller can read only the merchants they already have something at.
- `PendingBankLink`: a link interrupted between Plaid handing over an item and the app storing
  it; see [Linking](#linking).
- `BankMatchDismissal`: a row and an expense somebody said are not the same money.

Statuses are stored as strings, so a status column reads `Filed` in `psql`.

Rows stay staging data until filed because they are the bank's statement rather than
anybody's decision. A pending charge changes amount when it posts, the bank withdraws rows,
and a credit is not an expense at all. Kept apart from the ledger, a row can be rewritten by
every sync while the expense keeps the figures it was filed with: modifying a row never touches
the expense it became. `Transaction.BankTransactionId` is uniquely indexed, so a row files into
at most one expense, and it is `SetNull` on delete, so expenses outlive the bank they came
from.

### The jobs

Nothing waits on a sync inside a request. Syncs are jobs, dispatched through `IJobDispatcher`
from [GroupSplit.Jobs](../src/GroupSplit.Jobs/README.md) and registered in
[`BankingServiceExtensions`](../src/GroupSplit.API/Extensions/BankingServiceExtensions.cs):

| Job | Asked for by | Does |
| --- | --- | --- |
| `SyncBankConnection(id)` | linking, Sync now, refresh, two webhooks, the sweep | One connection's sync. |
| `SweepBankConnections` | daily at midnight UTC | Dispatches a `SyncBankConnection` per `Active` connection. |
| `SweepPendingBankLinks` | every five minutes | Dispatches `CompletePendingBankLink` or `AbandonPendingBankLink` per interrupted link. |

The sweep dispatches rather than syncing in place, so a slow bank holds up nobody else and each
connection fails on its own. By default the queue is an in-memory channel drained by a hosted
worker; a deployment that wants a queue service keeps the handler registrations and points the
dispatcher and receiver at that transport, as the Jobs README describes. A job that throws is
logged at error with its type and the record's `ToString`, which is why jobs carry ids and
nothing secret. There is no retry scheme: the next webhook, Sync now or tomorrow's sweep picks
the connection up again.

### The API

| Route | Name | Returns |
| --- | --- | --- |
| `GET /bank-connections` | `GetBankConnections` | The caller's connections, and `enabled`: whether a connector is registered. |
| `POST /bank-connections/link-token` | `CreateLinkToken` | A token for Plaid Link. Naming a connection opens update mode on it. |
| `POST /bank-connections` | `LinkBankConnection` | 201. Exchanges the public token, stores the item and its accounts, queues a sync. |
| `POST /bank-connections/{id}/refresh` | `RefreshBankConnection` | Re-reads the accounts after update mode and queues a sync. |
| `POST /bank-connections/{id}/sync` | `SyncBankConnection` | 202. Queues a sync. |
| `DELETE /bank-connections/{id}` | `UnlinkBankConnection` | 204. See [Unlinking](#unlinking). |
| `GET /inbox` | `GetInbox` | A page of rows, filtered by `status` (default `New`) and an optional `from`/`to` span, newest first, each with its possible duplicates. |
| `GET /inbox/summary` | `GetInboxSummary` | The waiting count for the badge; with `withDuplicates`, how many have a confident match. |
| `GET /inbox/{id}/matches` | `GetBankTransactionMatches` | Expenses this row could already be. |
| `POST /inbox/suggestions` | `SuggestBankTransactionFilings` | A suggested group and category for each of up to 50 rows that gets one. See [Suggested group and category](#suggested-group-and-category). |
| `POST /inbox/{id}/file` | `FileBankTransaction` | 201, the new expense. |
| `POST /inbox/{id}/link` | `LinkBankTransaction` | The existing expense, now linked to the row. |
| `POST /inbox/{id}/dismiss-match` | `DismissBankTransactionMatch` | 204. |
| `POST /inbox/{id}/ignore` | `IgnoreBankTransaction` | 204. |
| `POST /inbox/{id}/restore` | `RestoreBankTransaction` | 204. `Ignored` back to `New`. |
| `/inbox/{id}/receipt-attachments` | `...BankReceiptAttachment(s)` | Receipt files attached while a row waits; they move onto the expense when it is filed. |
| `POST /webhooks/{provider}` | `ReceiveProviderWebhook` | 200, 401 or 404. Anonymous, and left out of the generated client. |

The inbox is the date the row shows (`AuthorizedDate` where Plaid knows it, else the posting
date) all the way through: the span filters by it and the listing sorts by it.

Every read starts from the caller. A connection is reached through its owner and a row through
its account's connection, so another person's is a 404, never a 403, as everywhere else in the
API. A superseded row is a 404 too: it is not the caller's to act on either. A response never
carries the access token or the cursor. The codes are in [the error catalog](errors.md);
`BANK_PROVIDER_UNAVAILABLE` (502) is the one every call to Plaid turns into when Plaid fails,
and the only one that says retrying the same request later is right.

## Linking, syncing and re-linking

### Linking

The client asks for a link token, opens Plaid Link with it, and posts back the public token
Link returns. The token carries the webhook address when the deployment has one, so each item
is registered against the origin it was linked on rather than one address set in Plaid's
dashboard. A new link asks for 90 days of history (`Plaid:DaysRequested`) and for US
institutions (`Plaid:CountryCodes`). Banks that leave the page for their own sign-in are in
[Linking a bank that redirects](deployment.md#linking-a-bank-that-redirects).

The item exists at Plaid, and counts against the Plaid account, from the moment the person
finishes in Link, before the API has heard anything. So `Link` writes the public token
(encrypted) to a `PendingBankLink` before exchanging it, and swaps it for the exchanged item
before storing the connection. If storing then fails, the request answers
`BANK_LINK_WILL_BE_FINISHED` and the five-minute sweep finishes the link, which spends no
second item; after five attempts it gives up and hands the item back to Plaid. If the item
cannot even be written down, it is removed at Plaid there and then, and the code
(`BANK_LINK_NOT_SAVED` or `BANK_LINK_NOT_SAVED_ACCESS_REMAINS`) says whether that worked, which
is the one thing the person cannot find out anywhere else.

Linking a bank the person already has does not make a second connection. The same item is
found by its id. A fresh item at the same institution is recognised by account mask, with one
account in common enough, and the existing connection adopts it; see
[Re-linking](#re-linking). The mask and never the account name, because two separate logins at
one bank will both report "Checking", and a false match moves a working connection onto
somebody else's login.

### A sync

[`BankSyncService.SyncAsync`](../src/GroupSplit.API/Services/Banking/BankSyncService.cs):

1. Take the connection's lock. If a run already holds it, return: that run will see everything
   this one would.
2. Load the connection. One that is not `Active` is not synced, and nor is one whose provider
   has no connector here, such as the seeder's demo bank.
3. Decrypt the token. One the key ring can no longer open marks the connection `LoginRequired`,
   since nothing can recover it and the way out is linking the bank again.
4. Walk the pages from the stored cursor. Each page applies `Added`, then `Modified`, then
   `Removed`, and is saved as it arrives; the cursor is held in memory.
5. When Plaid says there is no more, store the cursor and `LastSyncedAt`.

Rows are saved per page and the cursor only at the end, which is Plaid's guidance. Every write
is an upsert keyed on the provider's id, so a crash part way leaves the rows and the old
cursor, and the next run walks the same pages over the same rows and lands in the same place.

`RestartFromCursor` starts the walk again from the cursor the run began with, at most three
times per run. Rows the abandoned walk inserted that the finishing walk never reports, and that
nobody has acted on, are deleted before the cursor moves; otherwise they would sit in the inbox
as money nobody spent, with the cursor past anything that could withdraw them.
`LoginRequired` marks the connection and ends the run. `Transient` ends it with the cursor
where it began.

Plaid reports a pending charge settling as a new posted row naming the pending one, followed
by a removal of the pending one. The posted row supersedes it: it takes the stronger of the
two statuses (`Filed` over `Ignored` over `New`), the expense's link when the pending row was
filed, and any receipt file attached to the pending row. The pending row becomes `Superseded`,
which no listing shows and no sync touches again. Neither is deleted, so the history reads
through `ReplacesId` and the inbox does not show the same coffee twice.

A removal deletes a `New` or `Ignored` row, with any receipt file attached to it in the inbox
and the stored object behind it. A `Filed` row is kept and stamped `RemovedAt`, because the
expense it became is somebody's spending history and the app should be able to say the bank
withdrew it. A `Superseded` row is left alone; its replacement holds the story.

A row on an account the connection does not know makes the run read the account list again,
once per run. A person can share another account in update mode, which mints no new item, so
this is the only way the app hears of it. If Plaid still does not hand the account over, its
rows are skipped and the connection flagged `AccountsNotShared`; only the person can share it.

A connection re-linked while its sync was running is checked for before every write to the
connection, and the run drops what it had to say about the old item rather than store a cursor
the new item has never heard of. The rows it imported stay.

One sync per connection at a time is a `SemaphoreSlim` per connection id in
[`BankSyncLocks`](../src/GroupSplit.API/Services/Banking/BankSyncLocks.cs). That is correct for
one API instance, which is what Compose deploys. A second instance needs a database lock, and
`TryHold` is the one place to put it.

### Re-linking

There are two ways back into a connection.

Update mode is Sign in again on the linked-banks card
([`LinkedBanksCard`](../src/GroupSplit.App/GroupSplit.App.Shared/Components/LinkedBanksCard.razor)
on the Account page). It asks for a link token naming the connection, and Link opens on the
existing item. That repairs a connection marked `LoginRequired`, renews a consent that is about
to expire, and shares an account left out at linking. Update mode mints no new item and hands
back no usable public token, so nothing on the linking path runs; the card follows it with
`refresh`, which re-reads the accounts, marks the connection `Active`, clears `SignInExpiring`
and `AccountsNotShared`, and queues a sync. Plaid sends nothing to confirm a renewed consent,
so a refresh clearing `SignInExpiring` is a guess, and the cost of a wrong guess is the
`LoginRequired` that arrives when the consent lapses anyway.

Linking the same bank again from scratch makes Plaid mint a new item, with new account ids and
new transaction ids. The existing connection adopts it: the token and item id are replaced,
its accounts are re-pointed by mask, the old item is removed at Plaid once the new one is
stored (in that order, so a failed save never leaves the connection naming a removed item), and
the cursor is cleared so the next sync fetches the whole history again. That history arrives
under ids the app has never seen, so `AccountsRekeyed` tells the next sync to recognise stored
rows by account, date, amount and description together and re-key them instead of inserting
copies. Without it every filed expense in that history would reappear in the inbox as new, and
the duplicate check could not catch one, because it compares against expenses that came from
no bank row. The match is exact on all four on purpose: a miss costs one extra row in the
inbox, while a false match re-keys a stored row onto a transaction that is not it and the real
one is never stored. The flag clears when a run completes.

### Unlinking

`DELETE /bank-connections/{id}` removes the item at Plaid first, then deletes the connection
with its accounts and rows. An item Plaid no longer knows counts as removed. Expenses filed
from those rows keep everything but the link back, merchant included. Receipt files still
waiting on inbox rows are deleted with their stored objects; files already on an expense stay
with it. A connection whose token cannot be read is unlinked here anyway and logged as an error
naming the item, which then has to be removed in Plaid's dashboard by hand.

## The inbox

In the app the inbox is `/transactions/not-filed` (`/inbox` still answers):
[`Inbox.razor`](../src/GroupSplit.App/GroupSplit.App.Shared/Pages/Inbox.razor). It is reached
from the waiting-on-you card on Home and from Activity, whose nav item carries the count of
waiting rows. The badge and the page read one `InboxStateService`, so opening the app costs one
summary call, and rows are not fetched until a page asks for them.

The filters are waiting (`New`), added (`Filed`) and ignored. A waiting row has one primary
action, Add to group, with Keep personal and Ignore behind a menu. A credit offers only Ignore.
An ignored row offers Put back.

### Filing

Add to group opens
[`FileBankTransactionDialog`](../src/GroupSplit.App/GroupSplit.App.Shared/Components/FileBankTransactionDialog.razor),
which is also the one screen recording any expense goes through (#251): Personal or a group,
the category, the shares, and a receipt. The bank's amount, date and currency are shown as text
rather than as disabled fields, because they are the bank's facts and not questions. When the row has a [suggested group and category](#suggested-group-and-category), both
arrive preselected. Otherwise the category is preselected when one of the group's category
names matches Plaid's category label (`FOOD_AND_DRINK` reads "Food and drink"). Reading a
receipt in the dialog asks for a suggestion again with the receipt's items, and moves the
selection unless the person has already picked a group or category themselves.

`POST /inbox/{id}/file` goes through `TransactionService.Create`, the same method a typed
expense takes, so the category's default split, the even fallback, membership and the
splits-must-sum rule come from there and are not written twice.
[`InboxService`](../src/GroupSplit.API/Services/Banking/InboxService.cs) adds only the guards
that make sense for imported money, and then the link:

- A row already filed is `BANK_TRANSACTION_ALREADY_FILED` (409).
- A credit is `BANK_TRANSACTION_IS_CREDIT` (422).
- A row in a currency other than the group's is `CURRENCY_MISMATCH` (409), carrying both
  currencies. The person's Personal group takes any currency, since nothing sums it with
  anybody else's.
- A row that looks like an expense already recorded is `POSSIBLE_DUPLICATE_EXPENSE` (409),
  carrying the matches, unless the request sets `fileAnyway`; see
  [Duplicate detection](#duplicate-detection). This is checked before the expense exists,
  because a duplicate found afterwards is a wrong balance somebody has to notice and undo.

The expense takes the row's amount, currency and merchant, the posting date at midnight UTC,
and the merchant name (or the bank's description) when the request names nothing. The row
becomes `Filed`, and receipt files attached to it in the inbox move onto the expense.

Keeping a row personal files it in the person's Personal group, whose id is their own user id
(#403). The request leaves the group out and the API reads that as Personal.

Filing is undone by deleting the expense, which puts the row back to `New`. A row the bank has
withdrawn since is deleted with the expense instead, since it would come back as a payment that
no longer exists.

### Linking to an expense already recorded

`POST /inbox/{id}/link` points the row at an expense the caller paid, instead of making a
second one. The expense keeps its amount, date and splits, which somebody wrote down on
purpose, and gains the link and the merchant. It is refused for a credit, a row already filed,
a currency mismatch, and an expense that already came from a bank row
(`TRANSACTION_ALREADY_IMPORTED`). When the file dialog opens on a row with a possible match, it
opens as that choice: attach to the expense, or record a new one (#285). The other order is
[`AttachBankRowDialog`](../src/GroupSplit.App/GroupSplit.App.Shared/Components/AttachBankRowDialog.razor):
right after somebody saves an expense, the app asks whether a waiting row is the same payment.

`POST /inbox/{id}/dismiss-match` records that the row and an expense are not the same money,
and the pair is never suggested again.

### Keep personal and ignore

Ignoring takes a row out of the inbox without making anything of it, and restoring puts it
back. Ignored rows stay ignored across syncs. A filed row cannot be ignored; deleting its
expense is what returns it.

Several waiting rows can be kept personal or ignored at once (#213). There is no bulk route:
the client calls the single-row endpoints in turn and reports once. Bulk keep-personal leaves
out credits and every row carrying a possible match, and says how many it left. Whether a row
is the same payment as an existing expense is answered one row at a time, and a bulk action
would have to assert `fileAnyway` on the person's behalf. Adding to a group is one row at a
time for the same reason.

### Suggested group and category

A waiting row can carry a suggested group and category (#437). The suggester is
[`FilingSuggester`](../src/GroupSplit.API/Services/Banking/Suggestions/FilingSuggester.cs),
which asks a Microsoft-Decision-1 deployment one multiple-choice question per row. The options
are every unarchived category in the caller's groups whose currency takes the row's (Personal
takes any), labelled `Group · Category`, plus `cannot tell`. The model judges the merchant, the
bank's description and categories, the amount and date, the account, what the caller's last
ten charges from the same merchant were filed under, and the receipt's merchant and item
names when the client sends them. Member names are not sent.

It answers with one option and a confidence. The answer is dropped when it is `cannot tell`,
when the confidence is under 0.6, or when it is not one of the options sent. A failed or slow
call (10 seconds) is dropped too, so the inbox looks as it did before suggestions existed.

`POST /inbox/suggestions` takes up to 50 rows, each with an optional receipt, and answers for
the ones that get a suggestion. Rows that are not waiting, credits, and rows with a possible
duplicate are never asked about. The listing does not carry suggestions, so the inbox never
waits on the model: `InboxStateService` asks once the rows are shown, remembers the answer for
each row, and does not ask about a row again when the inbox is read after a write.

On the row, the suggestion is a button naming the destination. Pressing it files the row
there with the category's split rule, the same request picking both in the dialog sends. Add
to group opens the dialog with the suggestion preselected.

The `azure-openai` resource is a Microsoft Foundry account reached by its key. The AppHost
models Decision-1 on it as a Foundry model, `filing-suggestions`, named by the parameter
`azure-openai-decision-deployment`, and the API
reads the endpoint, key and deployment from that connection. It runs only when the deployment
is set and Azure OpenAI is enabled. The call goes to
`{resource}/providers/microsoft/v1/systemone`, with the body sent whole: the service refuses
one without a `Content-Length`.

## Duplicate detection

Two paths put an expense in the ledger: somebody types one, and somebody files a row the bank
sent. Somebody records the dinner at the table so nobody forgets it, the card charge lands two
days later, and filing it would put the same dinner in the group twice, counted twice in every
balance with nothing saying so. Duplicate detection is a suggestion across those two paths and
two answers to it. Nothing is merged, hidden, filed or ignored until a person chooses.

### The refusal and its answers

Filing a row that looks like an expense already recorded is refused before a second expense
exists: `POSSIBLE_DUPLICATE_EXPENSE`, a 409, whose `matches` extension names the candidates
(see [the error catalog](errors.md)). The CLI exits 3 on it. The person answers by filing again
with `fileAnyway`, because they really did pay twice, or by linking the row to the expense that
is already there; see
[Linking to an expense already recorded](#linking-to-an-expense-already-recorded).

The other order is asked too. `GET /transactions/{id}/bank-matches` answers with the waiting
rows that could be an expense just recorded, and the app asks it the moment one is saved. A
pair somebody dismisses is a `BankMatchDismissal` row, and every suggestion query in both
directions filters through it, because a suggestion nobody can get rid of is worse than none.

### Two grades

The first version treated everything that passed the amount-and-date test as one claim, and
[#212](https://github.com/Sussman-Club/group-split/issues/212) measured what that cost; the
grades came in with PR #223. Over the 1,414 real expenses in one group, 11.5% of unrelated pairs
by the same payer passed the amount test on their own. A person types between 1.5 and 7
expenses into the eleven days the rule looks at, so between a fifth and well over half of
arriving rows raised a suggestion about money they had nothing to do with. On the demo data the
score was two suggestions, both wrong, and no real duplicate to catch. Exactness separates the
two claims:

| Grade | What it means | Coincidence rate | What it does |
| --- | --- | --- | --- |
| `Confident` | The amounts agree to the cent | 0.18% of pairs | A warning on the row, and a refusal on filing |
| `Possible` | They differ by something a duplicate could explain | 3.9% of pairs | A quiet note, and a refusal on filing |

The refusal is not graded. A tip added to a dinner is the likeliest duplicate there is, and it
is only ever a possible match, so a guard that wanted a confident one would wave the commonest
case straight through. The grade decides how loudly a suggestion is announced to somebody who
has not touched the row, and how the refusal's `detail` is worded ("This looks like an expense
you have already recorded" or "This could be..."). It does not decide whether the same money
can be recorded twice without a word.

### The rule

The rule is `DuplicateMatcher`, in
[`DuplicateMatching.cs`](../src/GroupSplit.API/Services/Banking/DuplicateMatching.cs), and
nowhere else. Callers ask for candidates and report what a person chose; none of them knows
what the window is. Tuning it is editing four constants at the top of that file:

| Constant | Value | Why |
| --- | --- | --- |
| `WindowDays` | 5 | A card charge posts days after the meal, and a weekend one waits until Tuesday. A window that caught only the same day would miss most real duplicates. |
| `RelativeTolerance` | 25% | A tip added after the receipt was written is the usual reason the amounts differ. Decides nothing on its own; see the shape rule below. |
| `AbsoluteTolerance` | 1.00 | So a €4 coffee is not held to a tolerance of pennies. Whichever of the two is more generous applies. |
| `MostSuggestions` | 3 | Somebody choosing between five candidates is not being helped by any of them. |

Two conditions hold on the sets before those numbers are consulted:

- The payer is the same person. A bank row is one person's card charge, so the only expenses
  it can be are ones they are down as having paid.
- The expense has no bank row the bank still stands behind. One whose row the bank withdrew,
  or that a posted row superseded without the link moving, is still a candidate: a pending row
  filed as an expense, withdrawn, and re-posted under a new id that does not name it is the
  same money arriving twice. Linking the new row to that expense moves the expense across and
  marks the old row superseded by the new one.

Then a pair has to be in the same currency and within the window, measured from the date the
row shows. Amounts equal to the cent are `Confident`. Otherwise the difference has to be within
tolerance, have a shape a duplicate could have, and not contradict the merchant, and then it is
`Possible`.

The shape rule exists because a tip only ever makes the card charge larger than the figure
somebody wrote down. Reading the tolerance in both directions cost half of everything the rule
raised, and both false suggestions the seeded inbox used to produce were a bank charging less
than the expense it was offered against. Two things do explain a lower charge, and both are
allowed. An expense on a whole unit may sit above the charge, because people round when they
type; 12% of real expenses are whole units, so this costs about a third of a percentage point.
And a pending row is often authorised before the tip is added, so for one of those the lower
charge is the expected direction.

The merchant is a condition in one direction. Both sides naming a place, and different places,
rules out a possible match; that alone settles the streaming subscription the seed used to
offer against a coffee run. It does not rule out a confident one, because one shop can end up
as two merchants (a chain and its subscription arm, a shop and its parent) and refusing a real
duplicate is the expensive mistake. One side knowing the place and the other not says nothing,
since most typed expenses have no merchant and never will. The bank's merchant text is not a
condition at all: "Dinner" against `SQ *TRATTORIA 4421` is the ordinary case.

Candidates are ranked confident before possible, then the same merchant, then nearest in
amount, then nearest in date, and last the bank's text reading like the expense's name. The
merchant outranks a nearer amount because ordering by closeness alone let a coincidence a few
cents nearer outrank the genuine duplicate with a tip on it, and the inbox leads with its best
candidate, so the real match was off the screen. Both directions use the same order.

The two directions ask about different rows. Filing is checked for any row that can still be
filed, ignored rows included: filing refuses only a row already filed, so a guard over waiting
rows alone would let an ignored row through. The other direction offers only waiting rows,
since handing back a row somebody put away is the suggestion nobody can get rid of. Credits are
never asked about; money coming in has no expense to be a duplicate of.

Matching runs in memory over a candidate set bounded by the window and the caller, a handful of
rows, so the arithmetic that decides somebody's balance stays readable instead of living in a
predicate written for the query translator. The inbox listing asks once per page, after paging,
rather than once per row.

### What the clients do with a grade

| | `Confident` | `Possible` |
| --- | --- | --- |
| Inbox row | A warning, with both answers | The same block in a plain voice, saying how far apart they are |
| Duplicate count on Home, `inbox summary` | Counted | Not counted |
| `groupsplit inbox list` | `(duplicate?)` | `(similar)` |
| `groupsplit inbox matches` | `(same amount)` | `(similar amount)` |

Every candidate is listed, not only the best one. Select-all ticks a flagged row like any other,
since a control called "select all" that quietly covers less than everything is worse than one
that says what it will not do. Bulk ignore includes flagged rows, because it creates nothing;
bulk keep-personal leaves them alone and says so ([Keep personal and
ignore](#keep-personal-and-ignore)).

Two things are left out on purpose. Nothing merges automatically: everything waits for somebody
to press something. And duplicates within one side are not looked for. Two typed expenses for
the same dinner are somebody's to notice, and two bank rows for one charge are the supersede
step in [A sync](#a-sync), decided by the provider's own `pending_transaction_id` rather than by
a person.

## Webhooks

Webhooks need an address Plaid can reach: a deployment is served on one, and locally a dev
tunnel supplies one. With no address nothing breaks. None is given to Plaid, a sync runs when
the app asks for one (on linking, refresh or Sync now), and the daily sweep catches whatever a
webhook would have announced.

The address is `Banking:PublicOrigin` plus `/webhooks/{provider}`, and a deployment sets it
from `web-hostname`: the same origin everything else is served on, whose `/webhooks` path the
web app forwards to the API. It is configured rather than read off the request because the
request's host is whatever the caller wrote in the `Host` header, and this address is handed
to a provider as where to deliver somebody's bank activity. A value that is not an absolute
HTTPS origin fails the start, because Plaid refuses such an address and the alternative is
finding out from the first person who tries to link a bank.

[`WebhooksApi`](../src/GroupSplit.API/Endpoints/WebhooksApi.cs) is the only anonymous route in
the API. Plaid has no account here and no token; what stands in for one is its signature over
the bytes it sent. The route answers 404 for a provider segment no connector is registered
under, caps the body at 128 KB before reading it, and hands the raw body and headers to the
connector before anything parses them. `PlaidWebhookVerifier` checks the ES256 JWT in
`Plaid-Verification`: the key for its `kid` (from `/webhook_verification_key/get`, cached until
it expires), an `iat` within five minutes, a `request_body_sha256` that matches the body, and
that the same webhook has not already been accepted. Failing any of those is a 401 and nothing
else happens. A verified webhook naming an item this deployment does not have is acknowledged
with 200, so Plaid stops retrying it.

| Plaid webhook | What happens |
| --- | --- |
| `TRANSACTIONS` `SYNC_UPDATES_AVAILABLE` | A sync is queued. |
| `ITEM` `NEW_ACCOUNTS_AVAILABLE` | `AccountsNotShared` is set, and the card puts Sign in again forward. |
| `ITEM` `PENDING_EXPIRATION`, `PENDING_DISCONNECT` | `SignInExpiring` is set. The connection stays `Active` and keeps syncing. |
| `ITEM` `USER_ACCOUNT_REVOKED` | That account is marked `AccessRevoked`; the rest of the connection carries on. |
| `ITEM` `ERROR` with a sign-in code | `LoginRequired`. |
| `ITEM` `LOGIN_REPAIRED` | `Active` again, `SignInExpiring` cleared, and a sync queued. |
| `ITEM` `USER_PERMISSION_REVOKED` | `Revoked`. |
| Anything else | Acknowledged and logged at information. |

A status is saved before any sync is dispatched. The worker reads the connection afresh, so a
sync asked for while `Active` was still uncommitted would read `LoginRequired`, refuse, and
leave everything behind the cursor until the nightly sweep.

### Receiving webhooks locally

`WebhookTunnel` in the AppHost's `appsettings.Development.json` puts the web app behind an
[Aspire dev tunnel](https://aspire.dev/integrations/devtools/dev-tunnels/) and sets the API's
`Banking:PublicOrigin` to the address it is given. It is `false` there; turned on, it appears
as the `webhook-tunnel` resource in the dashboard with the address it was handed:

```bash
dotnet user-secrets --project src/GroupSplit.AppHost set WebhookTunnel true
```

The tunnel fronts the web app rather than the API because that is the shape a deployment has:
Plaid calls the public origin and the `/webhooks` forwarder carries the call through unchanged,
so the one hop a webhook makes that nothing else does is exercised locally too.

It needs the `devtunnel` CLI and a signed-in account, which is half of why it is off until
somebody asks for it:

```bash
winget install Microsoft.devtunnel
```

```bash
devtunnel user login
```

The other half is that access has to be anonymous, and that reasoning covers `/webhooks` and
only `/webhooks`. The tunnel fronts the web app's root, so for as long as the run lasts the
whole local app (sign-in page, dev data and all) answers to anybody holding the address. Turn
it on while you are working on webhooks and off again afterwards. Aspire hosts the tunnel only
while the run lasts, but stopping the run does not delete it: the dev tunnels service keeps it,
anonymous access and all, until it has gone unhosted for its expiration. The AppHost sets that
to a day rather than the service's 30, so a tunnel left over from webhook work goes away on its
own (`devtunnel list` shows any that are still there).

## Turning it on

Locally, Plaid's sandbox needs no approval and opens any institution with `user_good` /
`pass_good`. The credentials go in the AppHost's user secrets, and `plaid-env` already defaults
to `Sandbox`:

```bash
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:plaid-client-id <id>
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:plaid-secret <sandbox secret>
```

Without them the inbox still has something in it: the seeder gives a demo user a bank under a
`seed` provider with rows waiting, which can be filed and ignored but never syncs, because no
connector answers for `seed`.

The API registers the Plaid connector whenever it has both credentials
([`PlaidServiceExtensions`](../src/GroupSplit.API/Extensions/PlaidServiceExtensions.cs)). There
is no enabled flag inside the API, so "configured" and "on" cannot disagree. The AppHost passes
the credentials through whatever `plaid-enabled` says, and that switch decides only what the
publish checks for; to turn bank sync off, leave the credentials empty. With no connector,
`GET /bank-connections` answers `enabled: false`, the card says bank sync is off instead of
offering a button that cannot work, and the routes that need a provider answer
`BANK_SYNC_UNAVAILABLE`.

A deployment sets `plaid-enabled`, `plaid-client-id`, `plaid-secret`, `plaid-env`, optionally
`plaid-redirect-uri`, and `bank-key-certificate`; they are in
[Deployment parameters](deployment.md#deployment-parameters).

The certificate is the requirement that is easy to miss. Access tokens are encrypted with
ASP.NET Data Protection, whose key ring lives in the app database, and a ring sitting beside
the ciphertext it opens would protect the tokens from a leak of one table and from nothing
else. So the ring is wrapped with a certificate the deployment holds as a secret. A publish
with bank sync on, or with both credentials set, and no certificate is refused by the
`validate-plaid` step, and outside Development the API refuses to start with Plaid configured
and an unwrapped ring. Locally the ring is unwrapped, with a warning at startup. Generating the
certificate, and what to do about a ring that was once stored unwrapped, are in
[The bank access-token key ring](deployment.md#the-bank-access-token-key-ring) and
[A ring that was stored unwrapped](deployment.md#a-ring-that-was-stored-unwrapped).

## What it does not do

- Credits. Money coming in (a refund, a deposit, a pay cheque) arrives in the inbox and can be
  ignored, and that is all. It cannot be filed or linked: an `Expense` is money going out,
  there is no income transaction yet, and a negative expense would be a number every total in
  the app had to know about.
- Rules by merchant. Nothing remembers that Lidl always goes to Home. Filing applies the
  category's default split, so a row needs only a group and a category, but a merchant rule is
  a rule table with a UI and belongs with recurring-expense detection.
- Balances. `LinkedAccount.Type` is stored as Plaid hands it over (`depository`, `credit`) for
  display, and nothing reads a balance.
- Conversion. A row in a currency other than the group's is refused with the same
  `CURRENCY_MISMATCH` a typed expense meets. It can be kept personal.
- More than one API instance, until the sync lock moves into the database; see
  [A sync](#a-sync).
- Rotating the key-ring certificate. There is nothing to rotate away from yet, and losing the
  certificate loses the stored tokens and nothing else: everybody links their bank again.

## The CLI and agents

`groupsplit bank` and `groupsplit inbox` cover the same routes. Linking runs in the provider's
browser UI, so the terminal half is the link token and the exchange afterwards; everything else
(refresh, sync, unlink, filing, linking to an expense, ignoring) is an ordinary command. See
[The `bank` commands](../skills/groupsplit/references/commands.md#bank).

An agent filing bank rows uses the
[groupsplit-inbox skill](../skills/groupsplit-inbox/SKILL.md), which covers the
`POSSIBLE_DUPLICATE_EXPENSE` refusal and when `fileAnyway` is the right answer. A change to
these commands moves `docs/cli.md` and the skill with it.
