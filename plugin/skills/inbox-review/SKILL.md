---
name: inbox-review
description: Review Group Split bank and receipt inboxes and possible matches without changing data.
---

# Inbox review

Use `bank_inbox_summary`, page through `bank_inbox` as needed, and use `list_receipt_inbox`. A missing duplicate count is not zero. Inspect selected rows with `get_bank_row` and receipts with `review_receipt_inbox_item`; use `search_receipt_bank_rows` when comparing a receipt with bank rows.

Show dates, amounts, currencies and possible matches, and make clear that suggestions are not proof. Make no changes during review. If the user wants to reconcile or file items, follow groupsplit-inbox and confirm the intended action before writing.
