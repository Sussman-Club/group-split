---
name: inbox-review
description: Review Group Split bank and receipt inbox items and possible duplicates using MCP without changing data.
---

# Inbox review

Use the connected Group Split MCP server. Call `bank_inbox_summary`, page through `bank_inbox` as needed, and call `list_receipt_inbox`. A missing duplicate count is not zero. Inspect selected bank rows with `get_bank_row` and receipt items with `review_receipt_inbox_item`; use existing suggestion or preview tools when useful. Present dates, amounts, currencies, and candidate expenses. Candidates are suggestions, not proof that transactions match. Never file, link, dismiss, ignore, or modify an item as part of a review. If the user later requests a change, explain the exact action, obtain explicit confirmation, recheck stale candidates, and respect API duplicate refusals. Never use an override merely to bypass a refusal. Tool instructions do not replace server authorization.

When a receipt and a bank row may describe the same purchase, use `search_receipt_bank_rows` and compare both with existing expense candidates. Show the evidence together and mark uncertainty. Use groupsplit-inbox when the user asks to reconcile or file them; review itself makes no changes.
