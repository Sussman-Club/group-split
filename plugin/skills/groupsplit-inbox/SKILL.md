---
name: groupsplit-inbox
description: Reconcile Group Split bank rows and receipts with each other and existing expenses using MCP.
---

# Reconcile the inbox

Follow groupsplit for authentication and write safety. A request to review is read-only: use inbox-review for the inventory. For selected bank rows inspect `get_bank_row` and `bank_row_matches`; for receipts use `review_receipt_inbox_item` and, when useful, `search_receipt_bank_rows`. Compare dates, merchants, amounts and currencies, including possible duplicate receipts and existing expenses. Candidates are suggestions, not proof. Page through results as needed.

Show the user the bank row, receipt and any existing expense together. Ask which are the same purchase and where it belongs. If an expense exists, propose `link_bank_row` and attaching the receipt with `file_receipt_inbox` to that expense. If neither has an expense, agree on one plan: `file_bank_row` then attach the receipt to the returned expense, or file the receipt as an expense and link the bank row to it. Do not perform both creation paths.

Confirm the exact action, destination and any balance effects before writing. These calls are not an atomic operation: check each result, recheck the target before the next call, stop on failure and report partial completion. Linking a receipt does not redivide an existing expense; preview and confirm any later division separately. Respect permission, currency, existing-attachment and duplicate refusals; never overwrite or bypass them.

If `file_bank_row` returns `POSSIBLE_DUPLICATE_EXPENSE`, review its candidates again. Use `fileAnyway` only when the user explicitly confirms the row is a separate payment. The receipt's duplicate override likewise requires confirmation of a separate purchase. `dismiss_bank_match` and `set_bank_row_ignored` require the user's choice. Never retry an uncertain write without inspecting state; one bank charge files as one expense.
