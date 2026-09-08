# The command surface

A map for planning, not the contract. `groupsplit schema --json` is the contract: it comes
out of the parser itself and is right for the version installed, while this file was written
against one. When the two disagree, the schema wins.

Regenerate the listing below from a built CLI with:

```bash
groupsplit schema --json | jq -r '
def leaf(prefix):
  (prefix + " " + .name) as $p
  | (if (.subcommands|length)==0 then
      "| `" + ($p|ltrimstr(" ")) + ((.arguments//[])|map(" <"+.name+">")|join("")) + "` | "
      + (.description // "") + " |"
     else empty end),
    ((.subcommands//[])[] | leaf($p));
.command.subcommands[]
| "\n### " + .name + "\n\n" + (.description // "") + "\n\n| Command | |\n| --- | --- |\n"
  + ([ leaf("") ] | join("\n"))'
```

## Global flags

Declared once and recursive, so they read the same at any depth: `groupsplit --json groups
list` and `groupsplit groups list --json` both work.

| Flag | |
| --- | --- |
| `--json`, `--output json\|text\|auto` | Force the rendering. `auto` is text on a terminal, JSON anywhere else. `--json` wins if both are given. |
| `--fields a,b` | Keep only these top-level fields, of an object or of every element of an array. Also accepts `--fields a b`. |
| `--server <origin>` | Override the configured server for one command. |
| `--profile <name>` | Read the server from a named profile. |
| `--quiet`, `-q` | Suppress progress and advisory messages on stderr. Errors still print. |
| `--no-color` | Disable colour. `NO_COLOR` set to anything does the same. |
| `--yes`, `-y` | Skip the confirmation gate. Read the rule in SKILL.md before you reach for it. |


### auth

Sign in to a GroupSplit server and inspect the current session.

| Command | |
| --- | --- |
| `auth login` | Sign in using the OAuth 2.0 device flow. |
| `auth logout` | Forget the stored credentials for this server. |
| `auth status` | Show who you are signed in as, and against which server. |
| `auth token` | Print the current access token, for piping into other tools. Treat it as a secret. |

### groups

Groups you belong to, their members and their balances.

| Command | |
| --- | --- |
| `groups list` | List every group you are a member of. |
| `groups show <group-id>` | Show one group. |
| `groups create <name>` | Create a group. |
| `groups rename <group-id> <name>` | Rename a group. |
| `groups members <group-id>` | List a group's members. |
| `groups remove-member <group-id> <user-id>` | Remove a member from a group. |
| `groups balances <group-id>` | Show who owes whom in a group. |
| `groups settle <group-id> <user-id> <amount>` | Record a repayment between you and another member. |
| `groups settle-between <group-id> <from-user-id> <to-user-id> <amount>` | Record a repayment between two named members, whichever of them you are — including neither. |
| `groups settle-up <group-id>` | Record every repayment between you and the rest of the group at once. |
| `groups activity <group-id>` | The group's ledger: everything that happened in it, newest first, with your share of each entry and your balance as at it. |
| `groups archive <group-id>` | Archive a group, hiding it from the active list. |
| `groups unarchive <group-id>` | Return an archived group to the active list. |
| `groups leave <group-id>` | Leave a group. Requires that your balance in it is settled. |
| `groups invite <group-id> <email>` | Invite people to a group by email. |
| `groups invitations <group-id>` | List the invitations a group is still waiting on. |
| `groups withdraw-invitation <group-id> <invitation-id>` | Withdraw an invitation nobody has answered. |
| `groups link show <group-id>` | Show a group's standing join link. |
| `groups link create <group-id>` | Make a join link for a group. Any link the group already had stops working. |
| `groups link revoke <group-id>` | Turn off a group's join link, so the URL stops working. |

### transactions

Expenses and settlements.

| Command | |
| --- | --- |
| `transactions list` | List transactions, newest first. |
| `transactions show <transaction-id>` | Show one transaction and how it was split. |
| `transactions create <name> <amount>` | Record an expense. |
| `transactions update <transaction-id>` | Change an expense. Only what you name is sent. |
| `transactions summary` | Total the transactions matching a filter. |
| `transactions monthly` | What you paid and what your share came to, month by month. |
| `transactions shares list` | List the expenses you owe a share of, newest first. |
| `transactions shares summary` | Total the shares matching a filter. |
| `transactions bank-matches <transaction-id>` | List imported bank rows that could be this expense arriving a second time. |
| `transactions delete <transaction-id>` | Delete a transaction: an expense, or a settlement. The only command under `transactions` that takes a settlement's id — get it from `groups activity`, since the listings here read the expenses and cannot see one. |

### users

The signed-in account and what it is owed.

| Command | |
| --- | --- |
| `users me` | Show the account this CLI is acting as. |
| `users position` | Show your overall balance across every group. |
| `users delete` | Delete the account you are signed in as. Permanent. |

### settle

Squaring up with a person, across every group at once. `groups settle` and
`groups settle-up` work inside a group, because a balance belongs to one; these work on a
person, because a payment does.

| Command | |
| --- | --- |
| `settle plan` | The fewest payments that leave you square everywhere, one line per person. |
| `settle pay <user-id>` | Record one payment between you and that person, wherever the debt between you lives. |
| `settle history` | Every repayment you were party to, in any group, newest first. |

### categories

Expense categories and their default split rules.

| Command | |
| --- | --- |
| `categories list` | List categories. |
| `categories create <name>` | Create a category. |
| `categories update <category-id>` | Change a category's name or its default rule. |
| `categories delete <category-id>` | Delete a category. |

### split-rules

Reusable rules describing how an expense is divided.

| Command | |
| --- | --- |
| `split-rules list` | List split rules. |
| `split-rules show <rule-id>` | Show one rule and the division it stands for. |
| `split-rules create <name>` | Create a split rule. |
| `split-rules update <rule-id>` | Change a rule's name or how it divides. |
| `split-rules delete <rule-id>` | Delete a split rule. |

### invitations

Group invitations addressed to you.

| Command | |
| --- | --- |
| `invitations list` | List invitations waiting for your answer. |
| `invitations accept <invitation-id>` | Accept an invitation and join the group. |
| `invitations decline <invitation-id>` | Decline an invitation. |
| `invitations link <link>` | Show which group a join link leads to, without joining. |
| `invitations join <link>` | Join the group a link leads to. |

### bank

Linked banks: what is connected, syncing it, and unlinking.

| Command | |
| --- | --- |
| `bank list` | List the banks you have linked, and their accounts. |
| `bank link-token` | Ask for a token that opens the provider's linking UI in a browser. |
| `bank link <public-token>` | Finish linking a bank with the token its UI returned. |
| `bank sync <connection-id>` | Ask for a fresh pull of transactions from a bank. |
| `bank unlink <connection-id>` | Unlink a bank and stop syncing it. |

### inbox

Imported bank rows waiting to be filed.

| Command | |
| --- | --- |
| `inbox list` | List imported rows, newest first. |
| `inbox summary` | Count the rows still waiting to be filed, and how many of those may already be recorded. |
| `inbox matches <row-id>` | List the expenses already recorded that an imported row could be. |
| `inbox file <row-id>` | File an imported row as an expense. |
| `inbox link <row-id> <transaction-id>` | Attach an imported row to an expense already recorded, instead of filing a second one. |
| `inbox dismiss-match <row-id> <transaction-id>` | Say an imported row and a suggested expense are not the same money. |
| `inbox ignore <row-id>` | Keep a row out of the inbox without filing it. |
| `inbox restore <row-id>` | Put an ignored row back in the inbox. |

### config

Read and write the stored server settings. Nothing here is baked into the binary.

| Command | |
| --- | --- |
| `config list` | Show the settings this invocation would use, and where each came from. |
| `config get <key>` | Print one stored setting. |
| `config set <key> <value>` | Store one setting in the current profile. |
| `config unset <key>` | Remove one setting from the current profile. |
| `config profiles` | List the configured profiles. |
| `config path` | Print the path of the config file. |

### completion

Print a shell completion script.

| Command | |
| --- | --- |
| `completion <shell>` | Print a shell completion script. |

### schema

Print the full command tree as JSON, including every flag, its type and its default.

| Command | |
| --- | --- |
| `schema` | Print the full command tree as JSON, including every flag, its type and its default. |

## The formats that are not obvious

### Dividing an expense: `split-rules create` and `update`

Four ways to divide, as flags. **Exactly one** may be given; passing two is refused rather
than resolved by precedence, because a caller who passed both had one of them in mind.

| Flag | Divides |
| --- | --- |
| `--even` | Equally. With no `--among`, between whoever is in the group at the time. |
| `--even --among <user-id> <user-id>` | Equally, but only between the members named. Repeatable. |
| `--payer` | Not at all: whoever paid owes all of it. |
| `--percent <user-id>=<percent>` | By percentage. Repeatable, decimals allowed: `--percent 8f0c...=33.33`. |
| `--shares <user-id>=<shares>` | By whole shares. Repeatable: `--shares 8f0c...=2`. |

A category carries a rule, and the rule is what divides an expense filed under it. So the
usual shape is: create the rule, create the category pointing at it, then file expenses under
the category.

```bash
groupsplit split-rules create "Two thirds me" --group $G --percent $ME=66.67 --percent $YOU=33.33 --json
groupsplit categories create "Rent" --group $G --rule $RULE_ID --json
```

### Setting exact shares: `--split`

`transactions update --split <user-id>=<amount>` and `inbox file --split <user-id>=<amount>`
take money, not percentages or share counts, and are repeatable. They set the division
outright. Omit the flag and the division is re-derived from the category's rule -- which is
what you want unless the user has given you per-person figures.

### Changing an expense

`transactions update` sends only what you name; everything else is left alone. The pairs that
mean "unset" are separate flags, because a flag with no value cannot say the difference
between "leave it" and "clear it":

| To | Pass |
| --- | --- |
| Move it to a group | `--group <group-id>` |
| Take it out of its group, back onto your own ledger | `--personal` |
| File it under a category | `--category-id <category-id>` |
| File it under nothing, so it divides evenly | `--no-category` |

### Before creating an expense

`transactions create ... --preview` shows the split the server would apply and creates
nothing. Use it whenever the division matters, and show the result before committing.

### Filtering and paging

`transactions list`, `transactions shares list`, `groups activity` and `inbox list` all take
`--page` (1-based) and `--page-size` (the server caps it), plus `--order asc|desc` and a
`--sort-by` whose keys differ per command:

| Command | `--sort-by` keys |
| --- | --- |
| `transactions list` | `dateTime`, `amount`, `name`, `category`, `group`, `paidBy` |
| `transactions shares list` | the same, plus `share` |
| `groups activity` | `dateTime`, `amount`, `name` |
| `inbox list` | `date`, `amount`, `merchant` |

`transactions list`, `transactions shares list` and `transactions monthly` also take
`--group`, `--from`, `--to`, `--search` and `--category`. The two `shares` commands add
`--owed-only`, which keeps just the rows that are actually a debt: your share of an expense
you paid for yourself is money you already have, not money you owe.

`groups activity` takes `--from`, `--to`, `--search` and `--kind` (`Expense` or `Transfer`).
`--kind` is what the group's two old tabs became: absent means everything, which is the
default because "what has happened here" does not distinguish. `--from`/`--to` are date-times there and calendar dates
(`2026-01-01`) on `inbox list`, which is what a bank puts on a row.

`transactions summary` and `transactions shares summary` take the same filters and answer
with totals instead of rows -- ask them rather than paging a list to add it up yourself.

### The two ways in to a group

`groups invite <group-id> <email>...` sends invitations to named people; the recipient
answers with `invitations accept` or `invitations decline`. A join link is the open door:
`groups link create` mints one, `groups link show` reads it back, `groups link revoke` turns
it off. On the receiving side, `invitations link <link>` says which group a link leads to
**without joining**, and `invitations join <link>` joins. Check before you join.

### Settling up

`groups balances <group-id>` says who owes whom. `groups settle <group-id> <user-id>
<amount>` records a repayment; `--direction` defaults to `theypaidyou`, the creditor's side,
so pass `--direction youpaidthem` when the user is the one who handed over the money. It is
confirmation-gated: it moves money in the ledger.

`groups settle-up <group-id>` records all of them in one go -- everything the user owes and
is owed in that group, each repayment exactly what `groups settle` would have written for
that pair. It settles the user's own position and nothing narrower, so it takes no amounts
and no way to narrow it; `--date` and `--note` go onto every repayment it writes. It reads
the balances first, so the confirmation lists the repayments themselves, and `--dry-run`
shows that list without writing anything. Being already square is success, not a refusal:
it says so and writes nothing.

It never records money moving between two other members. The user can say what they paid
and what they were paid, because they were there for both; a payment between two other
people is not theirs to state.

### Settling with a person, across groups

A balance belongs to a group; a payment belongs to a person. Somebody who owes the same
friend in two groups used to have to settle twice, and the `settle` commands are what close
that gap.

`settle plan` adds the per-group minimisation up by person: one line each, largest first,
naming the groups its figure comes from so the arithmetic is checkable on the row. The three
figures over the top are the same ones `users position` leads with.

`settle pay <user-id>` records one payment. `--amount` defaults to the whole of what is
outstanding between the two of them; `--direction` works exactly as it does on
`groups settle` and defaults to `theypaidyou`. Behind it the API writes one transfer per
group, in a single save, spending the amount over the groups **largest first**. That is not
a flag and should not be presented as a choice: which group a payment lands in is
bookkeeping, and the person handing over the money does not have to do it.

It reads the plan first, so the exit-4 confirmation lists the groups the payment will land
in and what each takes -- show those lines, they are the part the user cannot otherwise
see. `--dry-run` prints the same breakdown and writes nothing.

Being square with somebody is success, not a refusal: the command says
`"status": "nothing-outstanding"` and writes nothing. So is asking in the direction with
nothing outstanding in it -- which is the point of stating the direction rather than reading
it off a balance.

`settle history` answers "did I already pay this?", which is the question that stops people
settling twice. It spans groups, because a payment can.

A settlement recorded wrongly is taken back with `transactions delete <transaction-id>`,
which balances the ledger back to what it read before. Its id comes from `groups activity`:
the `transactions` listings read the expenses, so a settlement does not appear in
`transactions list` and `transactions show` will not describe one.

`groups leave` requires that the user's balance in the group is settled, so expect a refusal
until the balances are square.
