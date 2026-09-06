# Phase 3: bank data through Plaid

The implementation plan for [Phase 3 of the roadmap](roadmap.md#phase-3----plaid-3-weeks).
The roadmap settles *what* arrives and where the seam sits; this settles what each table
and interface looks like, how a sync behaves at its edges, what the API and the client
expose, and the order it is built in.

Read [Bank data without a provider in the
model](roadmap.md#bank-data-without-a-provider-in-the-model) and [Plaid
integration](roadmap.md#plaid-integration) first. Nothing here re-argues them.

## Scope

In:

- `BankConnection`, `LinkedAccount`, `BankTransaction`, and a nullable
  `Transaction.BankTransactionId` -- the one trace on the ledger of where a row came from.
- `IBankConnector`, with `PlaidConnector` the only implementation and `FakeBankConnector`
  in the tests.
- Link token, exchange, encrypted access tokens, a sync engine with a cursor per
  connection, pending -> posted, removals, and a background worker fed by webhooks, a
  nightly sweep and "Sync now".
- `/webhooks/{provider}` in the API and an anonymous forwarder for it in the BFF.
- An inbox: list what arrived, file a row into a group or keep it personal, ignore it,
  un-ignore it. Filing writes the splits through the same path a typed expense takes.
- A linked-banks card on the Account page with the re-login flow.
- Aspire parameters, the `validate-plaid` pipeline step, deploy docs.

Out, deliberately:

- **Credits.** A Plaid row with money coming *in* -- a refund, a deposit, a paycheque --
  arrives in the inbox and can be ignored, and that is all. It cannot be filed, because
  an `Expense` is money going out and an `Income` leaf is Phase 4. Refused with
  `422 BankTransactionIsCredit` rather than filed as a negative expense that every total
  would have to know about.
- **Auto-file rules by merchant.** The roadmap's review row mentions them; the phase list
  does not. What is built is auto-filing *once a row has a group and a category*: the
  category's default rule writes the splits with no further review. Remembering "Lidl
  always goes to Home" is a rule table with a UI and belongs with recurring detection.
- **MAUI.** The head does not reach the API at all today (no client options setter, an
  `AuthService` that throws). Hosted Link would be built on nothing. Phase 4, as the
  roadmap already has it.
- **Balances, and account types beyond a label.** `LinkedAccount.Type` is stored as Plaid
  hands it over (`depository`, `credit`, ...) for display. Nothing reads balances.
- **Conversion.** A row in a currency other than the target group's is refused by the
  same `409 CurrencyMismatch` a typed expense meets. The person can keep it personal.

## Decisions taken

**Plaid's API is spoken through Going.Plaid.** Plaid ships no .NET library of its own;
its libraries page names [Going.Plaid](https://github.com/viceroypenguin/Going.Plaid)
as the .NET option and, for anyone writing their own, says to generate it from
[plaid-openapi](https://github.com/plaid/plaid-openapi). Going.Plaid *is* generated
from that spec, so it is the spec-driven path with somebody else running the generator:
MIT, on .NET 10, a release in the week this was written, and registered from a `Plaid`
configuration section (`ClientId`, `Secret`, `Environment`) through its own
`AddPlaid`. It returns Plaid's error object rather than throwing, takes an
`IHttpClientFactory` so the tests can hand it recorded payloads, and covers the seven
calls the connector needs: `/link/token/create`, `/item/public_token/exchange`,
`/accounts/get`, `/transactions/sync`, `/item/remove`,
`/webhook_verification_key/get`, and in the sandbox `/sandbox/public_token/create`.
A community library is a dependency on one maintainer; the seam is what makes that
acceptable, because `PlaidConnector` is the only file that references it, and swapping
it for a generated client later touches that file and its tests. There is no Aspire
integration for Plaid, so the parameters and the `Plaid__*` environment variables are
wired by hand next to the SMTP ones.

**Routes are provider-neutral.** `/bank-connections` and `/inbox`, not `/plaid/...`. The
roadmap sketched the steps with Plaid's names because it was describing Plaid's flow;
the seam it also asked for means the UI must not change when a second provider arrives,
and a route named after the first one would. `Provider` travels as a field
(`"plaid"`), and the webhook route already carries it as a segment.

**Access tokens go through ASP.NET Data Protection, and the key ring is itself
encrypted.** `AppDbContext` implements `IDataProtectionKeyContext`, which forces the one
`DbSet` property the context otherwise avoids; the interface leaves no choice and the
remark on it says so. The ring lives in the app database so every instance reads what any
other wrote.

On its own that would be close to worthless, and the first version of this shipped exactly
that: a key ring in plaintext beside the ciphertext it opens, so a database dump carried
both. What closes it is `ProtectKeysWithCertificate`, with the certificate held as a
deployment secret the database never sees. Data Protection keeps doing what it is good at,
rotating keys and reading what older ones wrote, and the thing that unlocks it lives
somewhere else.

The alternative considered was dropping the key ring and encrypting each token with
AES-GCM under a passphrase. It was written and then abandoned: it avoided certificate
handling, which was the weaker argument, at the cost of hand-rolled cryptography guarding
bank credentials and a rotation story that had to be built by hand. Using the framework
is worth a one-time certificate.

Locally there is usually no certificate and the ring is unwrapped, which is the ordinary
development posture; the publish refuses a deployment that has bank sync on and no
certificate, because that deployment looks exactly like a correct one until somebody reads
the database.

**Statuses are strings in the database.** The first enums in the data layer, and the
precedent that gets set. `BankTransactionStatus` and `BankConnectionStatus` are stored
through `HasConversion<string>()`: a status column that reads `Filed` in `psql` is worth
more than the bytes an integer saves, and it matches how the discriminators already read.

**Amounts keep the sign they arrive with.** Plaid is positive for money leaving the
account; so is an `Expense`. `BankTransaction.Amount` is therefore the same number,
and the "map in one place" the roadmap asked for is `PlaidConnector` translating
Plaid's row into the seam's `ImportedTransaction` -- the sign convention is stated on
that type and enforced nowhere else. A negative amount is a credit, see Scope.

**A posted row supersedes its pending row; neither is deleted.** Plaid delivers
pending -> posted as a new row in `added` whose `pending_transaction_id` names the old
one, and then a `removed` entry for the old one. The posted row takes `ReplacesId`, and
takes over the pending row's status: `New` stays `New` under the new row, `Ignored`
stays ignored, and `Filed` moves the ledger link -- the `Transaction` now points at the
posted row. The pending row becomes `Superseded`, which no listing shows and no sync
touches again. That keeps the history the roadmap wanted `ReplacesId` for, and keeps the
inbox from showing the same coffee twice.

**A `removed` entry deletes a row nobody has acted on.** `New` and `Ignored` rows are
deleted; a `Filed` row is kept and stamped `RemovedAt`, because the expense it became is
somebody's spending history and the inbox needs to be able to say "the bank withdrew
this". `Superseded` rows are left alone -- the replacement holds the story.

**Rows are saved per page; the cursor only when the page run is complete.** Plaid's
guidance is to persist `next_cursor` only once `has_more` is false, and on any error
during pagination to restart the loop from the cursor the run began with. So each page's
rows are written as they arrive -- every write is an upsert keyed on the provider's id,
so a replay changes nothing -- and `Cursor` moves only at the end. A crash mid-run
leaves the rows and the old cursor, and the next run walks the same pages over the
same rows and lands in the same place.

**One sync per connection at a time, in process.** A `SemaphoreSlim` per connection id,
held by a singleton. That is correct for one API instance, which is what runs today and
what Compose deploys; a second instance needs a database lock, and the place to add it
is the one method that takes the semaphore. Written down here so it is not rediscovered
as a duplicate-row bug.

**Filing goes through `TransactionService.Create`.** "Add to group" builds a
`CreateTransactionRequest` from the row -- date, amount, currency, merchant, the chosen
group and category, optional stated splits -- and hands it to the same method the
dialog uses. Category default, even fallback, `CurrencyMismatch`, `SplitsDoNotSumToAmount`
and membership checks all come from there and are not written twice. The only thing the
inbox adds is `BankTransactionId` on the new expense and `Filed` on the row.

**Category suggestions are a label, matched by the client.** Plaid's
`personal_finance_category.primary` is stored in `ProviderCategory` and returned with a
readable label (`FOOD_AND_DRINK` -> "Food and drink"). A suggestion needs a group and the
inbox row has none until the person picks one, so the API does not guess; the dialog,
which has the group's categories loaded, preselects the one whose name matches. The
person can override, and the category that ends up on the expense is the one they sent.

**Webhooks are verified before they are parsed.** Plaid signs every webhook with an
ES256 JWT in `Plaid-Verification`; the connector fetches the key by `kid` from
`/webhook_verification_key/get`, caches it until `expired_at`, checks `iat` is within
five minutes and `request_body_sha256` matches the raw body. A webhook that fails any of
those is a 401 and nothing else happens. Four codes do anything: `SYNC_UPDATES_AVAILABLE`
enqueues a sync; `ERROR` with `ITEM_LOGIN_REQUIRED` marks the connection
`LoginRequired`; `LOGIN_REPAIRED` marks it `Active` again; `USER_PERMISSION_REVOKED`
marks it `Revoked`. Everything else is acknowledged and logged at debug.

**Sandbox locally, a switch in production.** `plaid-enabled` defaults to `false`, and
when `true` requires `plaid-client-id` and `plaid-secret`; `plaid-env` defaults to
`sandbox`. Same shape as `smtp-enabled` and `google-sign-in-enabled`, same
`validate-plaid` pipeline step, same `require_when` line in the deploy workflow. With
the switch off the API still starts: `GET /bank-connections` answers an empty list with
`enabled: false` in the envelope and the client hides the Link button. Locally the
credentials come from user secrets, and Link runs against the sandbox where
`user_good` / `pass_good` opens any bank.

## The schema

```csharp
public enum BankConnectionStatus { Active, LoginRequired, Revoked }
public enum BankTransactionStatus { New, Filed, Ignored, Superseded }

public class BankConnection : Entity
{
    public Guid UserId { get; set; }                 // an item belongs to a person
    public required string Provider { get; set; }    // "plaid"
    public required string ProviderItemId { get; set; }
    public required string InstitutionName { get; set; }
    public required string AccessTokenCiphertext { get; set; }
    public string? Cursor { get; set; }              // null: never synced
    public BankConnectionStatus Status { get; set; } = BankConnectionStatus.Active;
    public required DateTimeOffset LinkedAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public virtual ICollection<LinkedAccount> Accounts { get; } = [];
}

public class LinkedAccount : Entity
{
    public Guid BankConnectionId { get; set; }
    public required string ProviderAccountId { get; set; }
    public required string Name { get; set; }
    public string? Mask { get; set; }
    public required string Type { get; set; }         // as the provider says it
    public string? Subtype { get; set; }
    public string Currency { get; set; } = Currencies.Default;
    public virtual ICollection<BankTransaction> Transactions { get; } = [];
}

public class BankTransaction : Entity
{
    public Guid LinkedAccountId { get; set; }
    public required string ProviderTransactionId { get; set; }
    public required DateOnly Date { get; set; }
    public required decimal Amount { get; set; }      // > 0 is money out
    public string Currency { get; set; } = Currencies.Default;
    public required string Description { get; set; }  // the bank's line
    public string? MerchantName { get; set; }         // the provider's enrichment
    public string? ProviderCategory { get; set; }
    public bool Pending { get; set; }
    public Guid? ReplacesId { get; set; }
    public BankTransactionStatus Status { get; set; } = BankTransactionStatus.New;
    public DateTimeOffset? RemovedAt { get; set; }
    public required string RawJson { get; set; }      // jsonb in Postgres
    public required DateTimeOffset ImportedAt { get; set; }
}

public abstract class Transaction : Entity
{
    // ...
    public Guid? BankTransactionId { get; set; }      // set by filing; never by a sync
}
```

Mapping, in `AppDbContext`:

- `BankConnection` unique on `(Provider, ProviderItemId)` -- a bank linked twice is one
  connection, and the exchange endpoint answers the existing one -- and indexed on
  `UserId`, which is every listing's filter. Cascades from `User`: deleting an account
  takes its bank data with it, as the roadmap's "an item belongs to a person" implies.
- `LinkedAccount` unique on `(BankConnectionId, ProviderAccountId)`; cascades from the
  connection.
- `BankTransaction` unique on `(LinkedAccountId, ProviderTransactionId)` -- the dedup
  key every upsert relies on -- and indexed on `(LinkedAccountId, Status, Date)`, which
  is the inbox. `ReplacesId` is a self-reference with `SetNull`; the raw payload is
  `jsonb` in the Postgres context and text elsewhere. Cascades from the account.
- `Transaction.BankTransactionId` is `SetNull` on delete and uniquely indexed: a bank
  row files into at most one expense, and Postgres lets the nulls through.
- Strings: provider ids 128, names 128, `Mask` 8, `Type`/`Subtype` 32, `ProviderCategory`
  64, `Description` 256, statuses 16. Money `HasPrecision(18, 2)`; currency
  `character(3)` like everywhere else.
- `DataProtectionKey` is the framework's table, mapped by the package, named
  `DataProtectionKeys`.

The migration is `BankConnectionsAndImports`, purely additive: four tables and one
nullable column with no rows to backfill, so no data step and a `Down` that simply
drops them.

## The seam

```csharp
public interface IBankConnector
{
    string Provider { get; }                                  // "plaid"

    Task<LinkSession> CreateLinkSessionAsync(LinkSessionRequest request, CancellationToken ct);
    Task<LinkedItem> ExchangeAsync(string publicToken, CancellationToken ct);
    Task<SyncPage> SyncAsync(string accessToken, string? cursor, CancellationToken ct);
    Task RemoveAsync(string accessToken, CancellationToken ct);

    Task<bool> VerifyWebhookAsync(IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct);
    WebhookEvent ParseWebhook(string body);
}

public sealed record LinkSessionRequest(string ClientUserId, string WebhookUrl, string? RedirectUri, string? AccessToken);
public sealed record LinkSession(string Token, DateTimeOffset ExpiresAt);
public sealed record LinkedItem(string AccessToken, string ItemId, string InstitutionName, IReadOnlyList<ImportedAccount> Accounts);
public sealed record ImportedAccount(string ProviderAccountId, string Name, string? Mask, string Type, string? Subtype, string Currency);
public sealed record ImportedTransaction(string ProviderAccountId, string ProviderTransactionId, DateOnly Date,
    decimal Amount, string Currency, string Description, string? MerchantName, string? ProviderCategory,
    bool Pending, string? ReplacesProviderTransactionId, string RawJson);
public sealed record RemovedTransaction(string ProviderAccountId, string ProviderTransactionId);
public sealed record SyncPage(IReadOnlyList<ImportedTransaction> Added, IReadOnlyList<ImportedTransaction> Modified,
    IReadOnlyList<RemovedTransaction> Removed, string NextCursor, bool HasMore);

public abstract record WebhookEvent(string ProviderItemId);
public sealed record SyncUpdatesAvailable(string ProviderItemId) : WebhookEvent(ProviderItemId);
public sealed record LoginRequired(string ProviderItemId) : WebhookEvent(ProviderItemId);
public sealed record LoginRepaired(string ProviderItemId) : WebhookEvent(ProviderItemId);
public sealed record PermissionRevoked(string ProviderItemId) : WebhookEvent(ProviderItemId);
public sealed record UnhandledWebhook(string ProviderItemId, string Code) : WebhookEvent(ProviderItemId);
```

Connectors are keyed services, `AddKeyedScoped<IBankConnector, PlaidConnector>("plaid")`,
and the webhook route resolves by the route segment; an unknown segment is a 404 before
any body is read. Nothing above the seam sees an access token in the clear except the
sync engine at the moment it calls the connector.

`BankSyncException` is the one exception type the seam throws, carrying a `Kind`:
`LoginRequired` (the connection is marked and the sync ends), `RestartFromCursor` (the
mutation case), `Transient` (logged, retried next time). Anything else the connector
meets is a bug and propagates.

## The sync

`BankSyncService.SyncAsync(connectionId)`:

1. Take the connection's semaphore. If it is already held, return -- the running sync
   will see everything this one would.
2. Load the connection with its accounts. `Revoked` returns immediately;
   `LoginRequired` too, because the token no longer works and Plaid will say so.
3. Decrypt the token. Remember `startCursor = connection.Cursor`.
4. Loop: `page = connector.SyncAsync(token, cursor)`. Apply `Added`, then `Modified`,
   then `Removed`, in that order, and save the rows. `cursor = page.NextCursor`, in
   memory. Stop when `HasMore` is false; then set `Cursor = cursor`, `LastSyncedAt =
   now`, and save.
5. `RestartFromCursor` resets `cursor = startCursor` and continues; `LoginRequired`
   marks the connection and stops; anything else propagates and the cursor stays where
   the run began.

Applying a row:

- **Added.** Look up `(account, ProviderTransactionId)`. Present: treat as Modified
  (a replayed page). Absent: insert as `New`, then if `ReplacesProviderTransactionId`
  names a row on the same account, run the supersede step above.
- **Modified.** Update date, amount, currency, description, merchant, category, pending
  and raw payload. Status is untouched: a modified row that was filed stays filed and
  the expense keeps the amount it was filed with -- "re-syncing never touches the
  transaction".
- **Removed.** By status, as decided above.
- An account id the connection does not know is skipped and logged at warning: Plaid can
  add accounts to an item after linking (`NEW_ACCOUNTS_AVAILABLE`), and picking them up
  is a re-link, not a sync.

A sync is asked for as a job, `SyncBankConnection(connectionId)`, through the
`IJobQueue` seam in `GroupSplit.Jobs`. `POST /bank-connections/{id}/sync` and the webhook
both enqueue and return; nothing waits on a sync inside a request. The daily sweep is a
job too, `SweepBankConnections`, whose handler enqueues one sync per active connection,
and it is registered as recurring rather than run from a timer inside the sync code.

That seam is the reason the queue is not simply a `Channel<Guid>` in the sync service.
`GroupSplit.Jobs` holds the shape and nothing that runs it; `GroupSplit.Jobs.InProcess`
is the one implementation today, and `AddInProcessJobs` puts it in the API: an in-memory
queue, a pump draining it into the dispatcher, a timer for the recurring registrations.
A deployment that would rather have a queue service deliver to a function references a
sibling project instead of that one, keeps every `AddJob` call, registers the transport's
`IJobQueue`, and has the function hand each message to `IJobDispatcher`; the daily sweep
becomes a schedule that enqueues `SweepBankConnections`. The split follows the one
`GroupSplit.Data` and `GroupSplit.Data.PostgreSQL` already make.
The envelope is JSON on both sides -- even the in-process hop serializes -- so the job
types are proven on the wire from the first day rather than on the day the queue changes.
Failure has no retry scheme of its own: the transport redelivers by its rules, or the
sweep catches it tomorrow.

## The API

Two groups, and the anonymous route.

| Route | Name | Returns |
|---|---|---|
| `GET /bank-connections` | `GetBankConnections` | `BankConnectionsResponse { enabled, connections[] }` |
| `POST /bank-connections/link-token` | `CreateLinkToken` | `LinkTokenResponse { token, expiresAt }`; body may name a connection for update mode |
| `POST /bank-connections` | `LinkBankConnection` | `BankConnectionResponse`, 201; exchanges the public token, stores the item and its accounts, enqueues a sync |
| `POST /bank-connections/{id}/sync` | `SyncBankConnection` | 202 |
| `DELETE /bank-connections/{id}` | `UnlinkBankConnection` | 204; removes the item at the provider, deletes the connection; filed expenses lose their link and keep everything else |
| `GET /inbox` | `GetInbox` | `PagedResponse<BankTransactionResponse>`, `status` filter defaulting to `New`, sorted by date descending |
| `GET /inbox/summary` | `GetInboxSummary` | `{ newCount }` for the badge |
| `POST /inbox/{id}/file` | `FileBankTransaction` | `TransactionResponse`, 201 |
| `POST /inbox/{id}/ignore` | `IgnoreBankTransaction` | 204 |
| `POST /inbox/{id}/restore` | `RestoreBankTransaction` | 204; `Ignored` back to `New` |
| `POST /webhooks/{provider}` | -- | 200 / 401 / 404; anonymous |

`BankConnectionResponse` carries id, provider, institution, status, linked-at,
last-synced-at and its accounts (name, mask, type, currency). Never the token, never the
cursor. `BankTransactionResponse` carries the row's fields plus `categoryLabel`,
`accountName`, `institutionName`, `isCredit` and, when filed, `transactionId`.

New error codes, as built: `BankConnectionNotFound` and `BankTransactionNotFound` (404,
and a superseded row takes the second of those, because it is not the caller's to act on
either); `BankSyncUnavailable`, `BankTransactionAlreadyFiled`,
`BankConnectionNeedsAttention` and `CurrencyMismatch` (409);
`BankTransactionIsCredit` (422); and `BankProviderUnavailable` (502). That last one opens
the first 5xx category in the catalog, because it is the first time somebody else's
service is in the path and the first time retrying the identical request later is the
right advice.

`CurrencyMismatch` is the guard Phase 1 promised and never had a writer for. Filing is
where it finally gets one: a row in one currency cannot join a group keeping balances in
another, and the refusal carries both so a dialog can say which is which.

Every scoped read starts from the caller: a connection is `Where(c => c.UserId ==
me)`, a bank transaction is reached through its account's connection. Another person's
row is a 404, never a 403, matching the rest of the API.

## The client

- `IBankConnectionsClient` and `IInboxClient` fall out of the tags. `IBankCommands`
  wraps link, unlink, sync-now, file, ignore, restore in the `errors.TryAsync` shape,
  says what happened to what ("Lidl added to Home", "Bank of Sandbox linked"), and
  raises a third notifier event, `BankDataChanged`; filing also raises
  `TransactionsChanged`, because it is one.
- **Plaid Link** is `gs.plaid` in `gs.js`: `open(token, callbackRef)` loads
  `link-initialize.js` once, calls `Plaid.create` and forwards `onSuccess` /
  `onExit` to `[JSInvokable]` methods on a `PlaidLinkSession` object the page owns. The
  first `DotNetObjectReference` in the app; it is disposed with the page.
- `/inbox`, an entry in the nav before Account with a count from `GetInboxSummary`
  rendered as a small pill positioned absolutely inside the 46px item, so it cannot change
  the item's height and put the sliding highlight out of step. Each row: merchant,
  account, amount (credits muted and signed the other way), date, suggested label, and
  three buttons -- **Add to group**, **Keep personal**, **Ignore**. Filter buttons switch
  between waiting, added and ignored, so an ignored row can be put back.
- Filing into a group opens `FileBankTransactionDialog` rather than the existing expense
  dialog. The plan had it reuse that one, prefilled and locked; built, that meant a dialog
  whose amount, date and currency were all inert, which reads as a form somebody disabled
  rather than as facts the bank supplied. The dedicated one shows those as text and asks
  the only two questions that are the person's: which group, and what it was for.
- The badge and the page read one `InboxStateService`, not two of their own, so opening
  the app costs one summary call rather than one per reader. The rows are not fetched
  until a page asks for them.
- `ToMoney` gained a currency overload. Imported rows carry their own currency, and a euro
  charge shown with a dollar sign is a different number rather than a formatting quibble.
- Account page: a "Linked banks" card listing connections with institution, last sync,
  and a **Needs attention** state on `LoginRequired` that opens Link in update mode.
  **Link a bank** when the switch is on; a sentence saying bank sync is off when it is
  not.
- `PageStateRefreshTest` gets a sibling: filing from the inbox shows on Expenses and
  the group page without a reload.

## Tests

Above the seam, with `FakeBankConnector` scripting pages:

- a first sync lands every added row as `New`; a second sync with the same page changes
  nothing (dedup);
- a posted row with `pending_transaction_id` supersedes the pending one, moves a `Filed`
  link, keeps `Ignored` ignored;
- `removed` deletes a `New` row, stamps a `Filed` one, leaves a `Superseded` one;
- `Modified` changes the row and not the expense;
- filing into a group applies the category's default split; filing with no group is a
  personal expense with one split; filing a credit is refused; filing twice is refused;
- an ignored row stays ignored across a sync; restore brings it back;
- `LoginRequired` from the connector marks the connection and stops; a second sync while
  one is running returns without calling the connector;
- another person's connection and inbox rows are 404 over HTTP; the webhook route is
  reachable anonymously and refuses an unknown provider.

`PlaidConnector` on its own, with Going.Plaid pointed at a hand-written
`HttpMessageHandler` that answers from recorded sandbox payloads under
`tests/GroupSplit.API.Test/Banking/Plaid/Payloads`:

- two-page sync pagination with the cursor threaded through;
- `removed` and `pending_transaction_id` mapped onto the seam's records;
- amount and currency carried as-is; `personal_finance_category.primary` into
  `ProviderCategory`;
- webhook verification: a good signature accepted, a wrong body hash, an expired `iat`,
  an unknown `kid` and a non-ES256 header each rejected;
- the error envelope mapped to `BankSyncException` kinds: `ITEM_LOGIN_REQUIRED` and
  `TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION`.

`QueryTranslationTest` gains the inbox listing and its sort keys, because `DateOnly`
ordering and the status filter are exactly the kind of thing the in-memory provider
will accept and Npgsql might not.

## Order of work

Each step is a commit that builds and passes; the phase is one PR.

1. **This document.**
2. **Data.** Entities, context mapping, the Data Protection key context, the
   `BankConnectionsAndImports` migration.
3. **The seam and the sync.** `IBankConnector` and its records, `FakeBankConnector`,
   `BankSyncService`, the `GroupSplit.Jobs` seam with its in-process implementation and
   the two bank jobs, the tests above the seam.
4. **The API.** Token protection, `BankConnectionService`, `InboxService`, the two
   endpoint groups, the error codes, `docs/errors.md`.
5. **Plaid.** Going.Plaid registered from the `Plaid` section, `PlaidConnector`, webhook
   verification, the recorded-payload tests, the webhook route, the BFF forwarder,
   AppHost parameters and `validate-plaid`, the deploy workflow line and
   `docs/development-and-deployment.md`.
6. **The client.** Commands, Link interop, the inbox page and badge, the linked-banks
   card, page-state tests.
7. **Postgres.** `QueryTranslationTest` cases; run the stack, link a sandbox bank, watch
   a row travel from Plaid to a group.

## Follow-ons filed, not done

- A database lock in `BankSyncService` before a second API instance exists.
- `Income` as a third `Transaction` leaf, and with it filing credits.
- Merchant rules ("always file X to Y"), with recurring detection.
- Hosted Link for MAUI.
