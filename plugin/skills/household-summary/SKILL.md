---
name: household-summary
description: Summarize a Group Split group's spending, paid amounts and personal shares for a local calendar month.
---

# Household summary

Use the connected Group Split MCP server. Identify the group, month and time zone; use `list_groups` if needed and ask rather than guessing. Call `household_summary` with `groupId`, `year`, `month` and a time-zone ID such as `America/New_York`.

State the month and time zone. Show spending, the caller's paid amount and their share separately for each currency; do not add currencies. Explain that settlements are excluded. Distinguish an empty result from an error. This workflow is read-only.
