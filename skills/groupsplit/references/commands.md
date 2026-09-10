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
| `groups members <group-id>` | List a group's members, and the people it has invited and is waiting on. The `Status` column, and `isPendingInvitee` in JSON, say which. |
| `groups remove-member <group-id> <user-id>` | Remove a member from a group. |
| `groups balances <group-id>` | Show who owes whom in a group. |
| `groups settle <group-id> <user-id> <amount>` | Record a repayment between you and another member. |
| `groups settle-between <group-id> <from-user-id> <to-user-id> <amount>` | Record a repayment between two named members, whichever of them you are — including neither. |
| `groups settle-up <group-id>` | Record every repayment between you and the rest of the group at once. |
| `groups activity <group-id>` | The group's ledger: everything that happened in it, newest first, with your share of each entry and your balance as at it. |
| `groups archive <group-id>` | Archive a group, hiding it from the active list. |
| `groups unarchive <group-id>` | Return an archived group to the active list. |
| `groups leave <group-id>` | Leave a group. Requires that your balance in it is settled. |
| `groups invite <group-id> <name>...` | Name people the group is sharing costs with. Answers with a single-use link for each; there is no email. |
| `groups invitations <group-id>` | List the people a group is still waiting on, with their links and the ids to name them by. |
| `groups withdraw-invitation <group-id> <invitation-id>` | Withdraw an invitation nobody has answered. Confirmation required: their link stops working, and anything recorded against them goes to a member. |
| `groups link show <group-id>` | Show a group's standing join link. |
| `groups link create <group-id>` | Make a join link for a group. Any link the group already had stops working. |
| `groups link revoke <group-id>` | Turn off a group's join link, so the URL stops working. |

### transactions

Expenses and settlements.

| Command | |
| --- | --- |
| `transactions list` | The expenses **you** paid for, newest first. With `--group`, the group's whole ledger. |
| `transactions show <transaction-id>` | Show one transaction and how it was split. |
| `transactions create <name> <amount>` | Record an expense. |
| `transactions update <transaction-id>` | Change an expense. Only what you name is sent. |
| `transactions summary` | Total the expenses **you** paid for. With `--group`, the group's whole ledger. |
| `transactions monthly` | What **you** paid and what **your** share came to, month by month. Gross: no transfers. |
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
| `categories update <category-id>` | Change a category's name or its default rule. `--no-rule` clears the rule, which `--rule` cannot: a flag with no value cannot say the difference between "leave it" and "clear it". |
| `categories delete <category-id>` | Delete a category. |

### merchants

Places money gets spent, shared across every group, and their logos. A bank sync fills this
table on its own; these are the by-hand way in -- cash at the same shop every week, a group
with no bank linked, or a provider that named a place badly.

A merchant belongs to no group, so none of these take `--group`, and a rename is felt by
every group that has spent there. `merchants update --name` therefore asks first and its
confirmation carries the count of transactions behind the row; `--logo-url` alone does not
ask. `merchants delete` is refused while anything still points at the row, including a bank
row still waiting in an inbox.

| Command | |
| --- | --- |
| `merchants list` | List merchants, alphabetically. `--search` narrows on the name. |
| `merchants show <merchant-id>` | Show one merchant, with how many transactions point at it. |
| `merchants create <name>` | Add a place. `--logo-url` gives it a mark; without one it renders as initials. |
| `merchants update <merchant-id>` | Rename it, or change its logo. `--no-logo` clears the logo. |
| `merchants delete <merchant-id>` | Delete a merchant. |

To say where an expense was spent, name the merchant on the expense rather than here:
`transactions create --merchant-id`, or `transactions update --merchant-id` to add it
afterwards and `--no-merchant` to forget it. Filing a bank row sets it from the row itself,
so an imported expense already knows.

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

Personal invitation links and group join links you were sent.

| Command | |
| --- | --- |
| `invitations list` | The invitations whose links you have opened and not answered, with their tokens. Not "sent to me" -- there is no address to match. |
| `invitations show <link>` | Show what a personal invitation link leads to, without claiming it. Says nothing about the money. |
| `invitations claim <link>` | Claim it: join the group as the person it names, taking on the shares recorded against that name and their places in the group's split rules. Confirmation required. |
| `invitations decline <link>` | Decline it. Confirmation required: anything the group recorded against that name goes to a member of it. |
| `invitations link <link>` | Show which group a *join* link leads to, without joining. |
| `invitations join <link>` | Join the group a join link leads to, as yourself. |

`list` is the only command here that does not take a link, and it is how you find one: it
answers every invitation whose link this account has opened, so it is the way back to one
when the user no longer has the message. Everything else takes the link, and all of them
accept the whole URL as readily as the token inside it.

### bank

Linked banks: what is connected, syncing it, and unlinking.

A connection's status is `active`, `login required`, `revoked`, or one of two that still
sync: `account not shared` (the bank has an account this connection is not importing --
sign in again with it ticked, then `bank refresh`) and `sign-in expiring` (it works, but
not for much longer). An account's `Access` reads `withdrawn` when that one account was
revoked at the bank and the rest of the connection is fine.

| Command | |
| --- | --- |
| `bank list` | List the banks you have linked, and their accounts. |
| `bank link-token` | Ask for a token that opens the provider's linking UI in a browser. `--connection <id>` repairs an existing connection rather than linking a new bank. |
| `bank link <public-token>` | Finish linking a bank with the token its UI returned. |
| `bank refresh <connection-id>` | Re-read the bank's accounts and queue a sync. Run after an update-mode sign-in that shared another account: nothing else tells the app about it. |
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

`transactions list` and `transactions summary` add `--paid-by <user-id>`, which narrows to
one person's spending. Ids come from `groups members <group-id>`.

#### `--group` does two different things

Read the flag's own description in the schema rather than assuming, because one name covers
both behaviours:

| Command | `--group` means |
| --- | --- |
| `transactions list`, `transactions summary` | **the group's whole ledger** -- every expense in it, whoever paid |
| `transactions monthly`, `transactions shares list\|summary` | only *your* rows in that group |

The first two used to behave like the rest, which made them lie: they were the caller's own
expenses under a flag documented as a group filter, so a group of 1,411 expenses answered a
count of 2 to the member who had paid for two of them, and an $39,429.42 year of group
spending answered "nothing has been spent". A short answer from these is not evidence a
group is empty -- check which question you asked.

The two go together. If you list with `--group`, total with `--group`, or the page and the
figure beside it describe different sets.

A group the caller is not in -- including one they have **left** -- is refused with exit
code 3 and `GROUP_NOT_FOUND`, not answered with an empty list. Their own expenses in a group
they left are still in `transactions list`; the group's ledger is the group's.

`groups activity` takes `--from`, `--to`, `--search` and `--kind` (`Expense` or `Transfer`).
`--kind` is what the group's two old tabs became: absent means everything, which is the
default because "what has happened here" does not distinguish. `--from`/`--to` are date-times there and calendar dates
(`2026-01-01`) on `inbox list`, which is what a bank puts on a row.

`transactions summary` and `transactions shares summary` take the same filters and answer
with totals instead of rows -- ask them rather than paging a list to add it up yourself.

### The two ways in to a group

They are different doors and are not interchangeable.

A **join link** is the group's open door: `groups link create` mints one, `groups link show`
reads it back, `groups link revoke` turns it off. It is reusable, it expires, and it lets
whoever follows it in **as themselves**, claiming nothing. On the receiving side,
`invitations link <link>` says which group it leads to without joining, and
`invitations join <link>` joins.

A **personal invitation link** comes from `groups invite <group-id> <name>...`, one per
person. It hands over a *position in the group's ledger*: the shares recorded against that
name become the claimer's. So it works once, it says nothing about the money until claimed,
and `invitations claim <link>` is gated by the confirmation protocol. Never suggest
forwarding one, and never paste one where the group's join link would do.

### Somebody who has been invited and has not answered

`groups invite <group-id> <name>...` names people the group is sharing costs with. That makes
them somebody it can point at at once, so **use their id the way you would a member's**: give
them a share with `--split`, or name them with `--paid-by`. Do not wait for anybody to claim
anything, and do not leave them out of a division the user described as including them.

Their id is `participantUserId`, from `groups invitations`, and it is the id `groups members`
lists them under with `isPendingInvitee` true. It is **not** the invitation's own id, which is
only for withdrawing it, and not the token, which is the link to send them.

Two things you cannot do with them:

- **No repayment may name them.** `groups settle`, `groups settle-between`, `groups
  settle-up` and `settle pay` all refuse with `SETTLEMENT_WITH_PENDING_INVITEE`. Their
  balance is real and stands until somebody claims their link; there is simply no account to
  pay. If a user asks you to settle with one, say that rather than recording something else.
- **`groups remove-member` is not how they go.** It answers `GROUP_MEMBER_NOT_JOINED`, since
  there is no membership to remove. `groups withdraw-invitation` is the act.

**If the user has lost their own link**, `invitations list` has it -- provided they opened
it at least once. If they never did, only the group can send it again.

**The tokens are credentials.** A personal link is a claim on a position in the group's
ledger: whoever opens it takes on the shares recorded against that name. Hand one to the user
who asked for it and nowhere else -- never into a shared channel, a commit, an issue, or a
transcript you did not have to write it into. Say "send Carlos his link" rather than pasting
it unasked.

Claiming, declining and withdrawing all move money, and all three answer with what moved and
whose it is now. **Report that output**: it is somebody's balance that changed.

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

That absence is why no total from `transactions summary`, `transactions monthly` or
`transactions shares summary` is net -- with `--group` as much as without. Do not present
one as what somebody is owed or owes. `users position` is that answer; `groups balances
<group-id>` is it for one group; `groups activity <group-id> --kind Transfer` is the
transfers themselves.

`groups leave` requires that the user's balance in the group is settled, so expect a refusal
until the balances are square.
