---
name: household-summary
description: Summarize a Group Split group's spending, paid amounts, and personal shares for a local calendar month using MCP.
---

# Household summary

Use the connected Group Split MCP server. Identify the group and calendar month; use `list_groups` and paginate if necessary. Ask for an ambiguous group or unknown time zone rather than guessing. Call `household_summary` with `groupId`, `year`, `month`, and a time-zone ID such as `America/New_York`. Use the returned bounds and state the month and zone. Show category spending, the caller's gross paid amount, and their share for each currency separately; never sum different currencies. Explain that settlements are excluded and distinguish an empty result from an error. This is a read-only workflow; do not call write tools as part of it.
