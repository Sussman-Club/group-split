---
name: groupsplit-inbox
description: Reconcile Group Split imported bank rows and receipt inbox items through MCP, review duplicates and file or link only with an explicit user decision.
---

# Reconcile the inbox

Follow the groupsplit skill for authentication, tool discovery, permissions and write safety. For a read-only inventory use inbox-review; do not mutate anything merely because the user asks to review.

Use `bank_inbox_summary` and `bank_inbox`, paging through results when necessary. A null duplicate count means it was not requested, not zero. For each selected row inspect `get_bank_row` and `bank_row_matches` before filing; compare date, merchant, currency, amount and all returned candidates. A candidate is a suggestion, not proof. Check `list_receipt_inbox` and `review_receipt_inbox_item` for receipt details and candidate expenses. Use `suggest_bank_filings` or `suggest_receipt_inbox_filings` only as proposals; an empty suggestion is not evidence that filing is safe.

## Reconcile bank rows and receipts together

For each receipt, inspect its details, possible duplicate receipts and `receipt_inbox_matches`, then use `search_receipt_bank_rows` to find bank rows where useful; paginate the search. For each candidate bank row, inspect its status and expense matches with `get_bank_row` and `bank_row_matches`. Compare merchant, date, amount and currency; tips, refunds and pending versus posted rows can differ. Do not equate a match with proof or assume a bank row and receipt are linked directly.

Show the user the receipt, bank row and any existing expense together and ask which represent the same purchase. If an expense already exists, propose attaching the receipt to that expense and linking the bank row to it, subject to the API's restrictions. If neither has an expense and the user confirms both represent the same purchase, agree on one destination and create exactly one expense: file the bank row, then attach the receipt to the returned expense with `file_receipt_inbox` using its `expenseId`; alternatively file the receipt as one expense and link the bank row to that returned expense. Do not perform both creation paths. Each is a separate write, not an atomic operation: confirm the complete plan in advance, verify each result, stop on any failure, and report partial completion. Recheck the target before the second write. An existing receipt or attachment, currency mismatch, permission error or duplicate refusal can prevent the second step; never overwrite or bypass it. Linking a receipt does not redivide an existing expense. Preview and separately confirm any later division.

Present the choices and let the user decide:

- Same purchase: `link_bank_row` to the identified existing expense. Linking does not change that expense's amount, date or split; a correction is a separate request.
- Distinct purchase: after the user approves the destination and split, use `file_bank_row`. A `POSSIBLE_DUPLICATE_EXPENSE` refusal requires renewed review. Set the request's `fileAnyway` override only when the user explicitly confirms that it is a separate payment; never use it as a generic confirmation or to bypass a refusal.
- Wrong match: after approval, `dismiss_bank_match` dismisses that pair and leaves the row waiting. Ignoring or restoring a row with `set_bank_row_ignored` also requires the user's choice.
- Receipt inbox item: review candidates before `file_receipt_inbox` and present whether it will attach to an existing expense or create one. A duplicate override requires explicit confirmation that it is a separate purchase. Do not confuse adding a file with saving itemized details or applying a division.

Obtain confirmation of the exact selected action, destination, amount and any balance effect before writes. Recheck if data may be stale, respect currency and authorization errors, and do not automatically retry after an uncertain response. One bank charge files as one expense; do not promise to split a single charge across multiple expenses. Report actual results and any failures separately.
