namespace GroupSplit.API.Services.PersonalData;

internal static class ExportReadme
{
    internal const string Text = """
        GroupSplit personal data export - schema version 1

        data.json is the canonical UTF-8 record archive. schema.json describes its
        JSON Schema 2020-12 structure. IDs join related records. Money, quantities
        and tax rates are decimal strings with a dot and no grouping separators.
        Use decimal arithmetic. Currency codes are ISO 4217; receipt amounts and
        final shares inherit their transaction's currency. Do not sum currencies.
        Dates and timestamps use ISO 8601. Enums are readable camel-case strings.
        Null means absent/not applicable; empty arrays mean no visible records.

        tables/transactions.csv and tables/splits.csv are convenience ledger views:
        UTF-8 with BOM, comma delimiters, CRLF and RFC 4180 quoting. Text that could
        be a spreadsheet formula has an apostrophe prefix; JSON preserves exact
        originals. CSV empty cells do not distinguish null from empty text. Import
        ID columns as text. Spreadsheet formula handling varies; use JSON for imports.

        receipts/ contains original bytes named by attachment ID and MIME extension.
        Attachment metadata maps paths to original filenames, MIME types and sizes.
        Original filenames never control archive paths. manifest.json lists every
        other file's path, uncompressed size and SHA-256. These hashes detect accidental
        corruption; they are not signatures or proof of origin.

        Scope: your profile/memberships; visible groups/categories/expenses/transfers
        and final shares; people named by the ledger or settlement requests (no other people's email);
        structured receipts/items/files; your private banks/accounts/imported rows;
        settlement requests you are party to. Visibility matches the regular API.
        Membership history grants no extra access. Credentials, bearer tokens, bank
        raw payloads/cursors, identity subjects, storage keys and unrelated private
        records are excluded. Rule configuration/version history is not included;
        final shares are. This is not an application backup.

        A missing/failed receipt aborts export before a ZIP is returned; retry.
        Keep this archive private: it includes financial records and shared receipts.
        """;
}
