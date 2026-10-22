# Personal data, export and deletion

## Export format (version 1)

Authenticated `GET /users/me/export` downloads `groupsplit-personal-data.zip` with
`Cache-Control: no-store`. It accepts no user ID: the caller can export only their own
view. `data.json` is UTF-8 JSON with camel-case properties and `schemaVersion: 1`.
Dates are ISO 8601. Decimal values (money, quantities, rates) are exact decimal strings
with a dot and no grouping separators, avoiding binary floating-point rounding in consumers.
Currencies are ISO 4217, without conversion. Enum values are camel-case strings.
The archive includes a generated JSON Schema 2020-12 (`schema.json`), a self-contained
`README.txt`, and `manifest.json` listing each other file's uncompressed size and SHA-256.
Checksums detect corruption and are not signatures or proof of origin.

The archive contains the profile; currently visible groups and categories; the caller's
membership history; visible expenses and transfers with payer IDs, amounts, currencies
and final shares; structured receipts and line items; the caller's bank connections,
accounts and imported rows; settlement requests they are party to; names of referenced
people (excluding others' email); and receipt metadata. Own bank rows include filed-expense
and replacement IDs. Files use `receipts/{attachmentId}.{safeMimeExtension}`; metadata gives
their exact archive path, original filename and MIME type. Receipt amounts inherit their
expense's currency. Transaction visibility is the
normal activity API's (current groups plus transactions the caller paid for). Receipt
visibility is the normal receipt API's. Departed groups do not grant access to other
people's newer records. Membership history does not grant access to those groups.
Transactions include group/category labels even when their payer has left the group and
its full metadata is no longer visible.

`tables/transactions.csv` and `tables/splits.csv` provide spreadsheet views (UTF-8 with BOM,
comma delimiters, CRLF, RFC 4180 quoting). Formula-like text has an apostrophe prefix;
canonical JSON preserves original text. CSV empty cells cannot distinguish null from empty
strings. Import IDs as text. Spreadsheet formula handling varies; use JSON for programmatic
imports. On Android, open Account on the website and sign in there to download the archive.

Other people's private bank connections, accounts and inbox rows are excluded, even
when their filed expenses are visible. The export excludes credentials, identity subjects,
bank token ciphertext, provider raw JSON, provider cursors, invitation/request bearer
tokens, receipt object keys and signed storage URLs. It is a portable record archive,
not a database backup or a promise to reconstruct every internal rule/version.
An unavailable receipt fails the export; retry rather than accepting a silently incomplete
archive. The ZIP is staged in a private temporary file, deleted when the response closes
or generation fails. Ledger collections stream from the database; only the attachment
inventory and one receipt file at a time are explicitly buffered (the database retry strategy
may also buffer query results). The browser buffers the completed ZIP for saving. Relational reads use a repeatable-read
snapshot so CSV/JSON record views agree. Original file availability is checked separately.

### Format decisions

ZIP combines structured records and original files using ordinary extraction tools. JSON
preserves relationships and missing values; CSV is a convenience view rather than the
canonical representation. Decimal strings follow the interoperability concern in
[RFC 8259](https://datatracker.ietf.org/doc/html/rfc8259). CSV quoting follows
[RFC 4180](https://www.rfc-editor.org/rfc/rfc4180.html), with formula precautions informed by
[OWASP](https://community.owasp.org/attacks/CSV_Injection). The checksum inventory is inspired
by [BagIt](https://www.rfc-editor.org/info/rfc8493/); this archive does not claim BagIt compliance.

## Account deletion

Deletion, bank linking (including pending-link completion), bank unlink and receipt uploads
share an account guard. Writers re-read the stored account status after acquiring it; a
request authenticated before deletion cannot add private records after the tombstone is
committed. An upload/link already in progress finishes before deletion inventories its data.
Pending links found for a deleted account are abandoned rather than completed.

The production guard uses a PostgreSQL session advisory lock on a dedicated unpooled
connection, spanning the workflow's multiple saves and provider/storage calls. Disposing
that connection releases the lock on success, failure or cancellation. This requires a direct
PostgreSQL connection or session pooling; do not route it through PgBouncer transaction or
statement pooling. Each guarded operation consumes one additional connection while running.
See [PostgreSQL advisory locks](https://www.postgresql.org/docs/current/explicit-locking.html#ADVISORY-LOCKS).
In-memory test hosts use a process-local equivalent; relational concurrency tests use real
PostgreSQL sessions. No new database schema is required for this coordination.

This is cooperative coordination: every path that creates these private records must use
the guard. It is not a database constraint, and losing the lock session while work continues
on another connection can release coordination early. A durable `Deleting` workflow with
short guarded transactions and retryable cleanup would provide a stronger recovery model.
That broader lifecycle change is outside this targeted race fix.

An account concurrency token alone does not protect child inserts
([EF concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)). One database
transaction around bank and storage calls would hold locks across network operations;
EF advises keeping such transactions short. A durable cleanup/outbox workflow is the
larger alternative ([transactional outbox](https://learn.microsoft.com/en-us/azure/architecture/databases/guide/transactional-out-box-cosmos)).
PgBouncer explicitly lists session advisory locks as incompatible with transaction pooling
([feature matrix](https://www.pgbouncer.org/features.html)).

Outstanding shared balances block deletion before any cleanup. Once settled, bank unlink
runs first. A provider error leaves the account active and the remaining connection in
place: retry deletion (banks already removed stay removed). A lost key or missing connector
cannot remove a provider Item, so its provider, Item ID, connection ID and reason are stored
for an administrator in `PendingDataCleanup`, without access tokens.

Names and email addresses are cleared and shared memberships closed. The user ID remains
as a tombstone. Shared expenses, final shares, settlements, structured receipts and shared
receipt files remain as ledger history. Personal ledger records also remain under the
tombstone, but personal receipt files are removed. Free-text ledger descriptions and shared
receipt images are retained as entered; deletion does not redact their contents.
Bank accounts, imported bank rows and unfiled receipt files are removed. Filed expenses
lose their private bank-row link. Pending bank links are abandoned; unresolved provider
cleanup is recorded. The Keycloak login (including its sessions and linked identities) is
deleted using the existing `PendingLoginDeletion` retry workflow; the tombstone immediately
refuses sign-in even if Keycloak is temporarily unavailable.

Backups are controlled by the deployment operator. The application enforces no automatic
backup expiry and cannot promise a fixed number of days. Operators must document and
configure database, Keycloak and object-storage backup/version expiry, restrict access to
backups containing credentials, and reapply completed deletions after any restore before
reopening access. See [deployment guidance](deployment.md).

## Operational reconciliation

Inspect `PendingDataCleanup` regularly. `provider-item` rows name the provider Item to
remove through its administrative tooling. `pending-link:{id}` means an encrypted held
item was unreadable; reconcile provider inventory with the held-link ID and timestamp.
Do not print or copy access tokens into logs or tickets. Missing connectors must be restored
or the Item removed through the provider dashboard. Delete the cleanup row only after
confirming removal with the provider; retain a credential-free operational audit externally.

`receipt` rows name object keys awaiting deletion. Delete each from the configured receipt
bucket (including retained versions under the deployment's policy), verify absence, then
remove the row. Deleting an already absent object is safe. Receipt deletions persist work
before contacting storage; account deletion and bank unlink persist that work alongside
row removal. Other historical receipt paths may still have a crash window before queuing.
Storage errors are logged with object keys and leave the rows actionable. Keycloak failures
continue through `PendingLoginDeletion` and its existing sweep. None of these operational
tables is exposed in a user's export.
