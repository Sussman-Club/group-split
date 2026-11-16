---
name: groupsplit
description: Use Group Split MCP for groups, expenses, balances, settlements, receipts, members, split rules, categories, banking, support and staff workflows. For bank-row reconciliation also use groupsplit-inbox.
---

# Group Split through MCP

Use the connected Group Split MCP server, not the removed command-line application. Discover tools and their input schemas from the connected server; the server's current schema is authoritative. If it is unavailable or authentication fails, report that and ask the user to connect or sign in through the host. Never request or paste bearer tokens. API permissions and `mcp:read` / `mcp:write` are enforced by the server, not by this skill; a preview or a prompt does not grant permission.

## Reads and scope

- Identify the signed-in account with `current_user`, find accessible groups with `list_groups`, and inspect a group with `get_group` or `group_members`. Page through listings as needed and disambiguate groups or people rather than guessing.
- Use `group_balances` for a group's balances and `user_position` for the caller's position across groups. For expenses use `list_group_expenses`, `list_expenses`, `get_expense`, `expense_shares`, and their summaries as appropriate. Payer totals, personal shares, balances and settlements answer different questions; do not substitute one for another or sum currencies.
- For a calendar-month household report, follow the household-summary skill and use `household_summary` with an explicit time zone. For a bank or receipt inbox, route to groupsplit-inbox; a review alone stays read-only as described in inbox-review.
- Discover group, invitation, category, merchant, split-rule, support and staff tools from their descriptions. Staff tools also require API staff roles. Do not assume a CLI feature exists over MCP; if a capability or client attachment transfer is unavailable, explain the limit instead of fabricating an action.

## Changes and previews

Act on the user's requested change only. Before recording an expense, establish the payer, group, amount, currency, date and intended split; use `preview_expense` when the split needs review, then `create_expense` once authorized. For edits, inspect the existing expense and use `preview_transaction_update` where applicable; explain changes to splits and balances before confirmation. For settlements use `settlement_plan` or group balances, show the exact people, direction, amounts and affected groups, and obtain explicit confirmation before recording. For receipt itemization inspect the saved receipt, use `preview_receipt_division`, and obtain explicit confirmation before saving or dividing shares.

Obtain explicit confirmation of the exact consequences before destructive, balance-changing, duplicate-prone, or externally consequential operations. Review a merge with `staff_preview_merchant_merge` before a confirmed merge. Invitation and settlement links grant access: share only at the user's direction. A confirmation is not an API-enforced protocol; obey the host's own approval controls and API refusals. Never automatically retry an uncertain write: first inspect current state, because it may already have succeeded. An explicit user request to message others is required before using messaging tools.

Receipt uploads require actual file bytes, not local paths or URLs; respect the tool's size and media-type constraints and the host's attachment support. Only send content for transcription with user authorization and disclose that it is sent to the configured transcription service. Never infer that a file was attached or saved from a preview.
