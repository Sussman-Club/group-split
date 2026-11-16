---
name: groupsplit
description: Use Group Split MCP to work with groups, expenses, balances, settlements, receipts and related records.
---

# Group Split

Use the connected Group Split MCP server. Discover its current tools and input schemas; if it is unavailable, ask the user to connect or sign in through the host. Never request bearer tokens. Use `current_user` and `list_groups` when needed, page through results, and clarify ambiguous people or groups.

For balances use `group_balances` or `user_position`; for expenses use `list_group_expenses`, `list_expenses`, `get_expense` and `expense_shares`. Keep currencies separate and distinguish amounts paid, shares and balances. Use the household-summary skill for a local month, and groupsplit-inbox for bank or receipt reconciliation. Discover other group, member, split-rule, category, merchant, support and staff tools as needed; staff access is enforced by the API.

For a new expense, check its payer, amount, currency, date, group and split. Use `preview_expense` when a split needs review, then `create_expense` if authorized. Before changing a transaction or receipt division, inspect it and preview where possible. Before a settlement use `settlement_plan` or group balances and show the direction, amount and affected groups.

Get explicit confirmation before destructive, balance-changing or duplicate-prone actions. Do not retry an uncertain write without checking whether it succeeded. API authorization and the host's approval controls still apply; a skill or preview does not grant permission. Invitation and settlement links are sensitive; share only at the user's direction. Receipt uploads require file bytes, not paths or URLs. Obtain authorization before sending a receipt to a transcription service and explain where it goes.
