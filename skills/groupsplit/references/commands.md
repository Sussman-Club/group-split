# The command surface

A map for planning. `groupsplit schema --json` is the contract: it comes out of the parser
itself and matches the version installed, while this file was written against one. When the
two disagree, the schema wins.

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

## Amounts

Every amount -- an `<amount>` argument, `--amount`, `--subtotal`, `--tax`, `--tip`, `--total`,
the value in a `--split` or `--percent` pair, and the numbers in a receipt `--item` -- is
`12.50`: a `.` decimal point, no thousands separators, at most two decimal places (a receipt
quantity may have three). The same on every machine. Anything else is exit code 3 before any
request: `12,50`, `1,234.56`, `1.234,56` and `12.555` are all refused, never rounded.

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
| `groups list` | List every group you are a member of. Your personal group comes first, state `personal` (`isPersonal` in JSON); its id is your user id. |
| `groups show <group-id>` | Show one group. |
| `groups create <name>` | Create a group. |
| `groups rename <group-id> <name>` | Rename a group. |
| `groups set-default-split <group-id> <rule-id>` | Choose what an uncategorized expense in this group divides by. Refused for the group's "divide it by the bill" rule (`SPLIT_RULE_INVALID`). |
| `groups set-default-period <group-id> <period>` | Choose which span the group's Activity tab opens on in the app, for every member: `all-time`, `this-month` (the default), `last-month`, `last-3-months` or `this-year`. `groups show` prints it; JSON carries it as `defaultPeriod`. Nothing in the CLI reads it. |
| `groups members <group-id>` | List a group's people: its members and its guests. The `Status` column, and `isGuest` in JSON, say which. |
| `groups remove-member <group-id> <user-id>` | Remove a member or a guest. Requires that their balance in it is settled (`GROUP_MEMBER_NOT_SETTLED` otherwise, with the balance). Removing a member stops the group's join link, and they need a link issued afterwards to come back; removing a guest leaves the join link alone and keeps them as a past guest. Somebody not on the roster -- including anyone who has already left -- is refused with `USER_NOT_FOUND` before any confirmation is asked for. |
| `groups balances <group-id>` | Show who owes whom in a group. `--through <day>` shows it for activity dated up to that day. |
| `groups settle <group-id> <user-id> <amount>` | Record a repayment between you and another member. `--through <day>` records the period it was for; the amount is still the one given. |
| `groups settle-between <group-id> <from-user-id> <to-user-id> <amount>` | Record a repayment between two named members, whichever of them you are — including neither. |
| `groups settle-up <group-id>` | Record every repayment between you and the rest of the group at once. `--through <day>` settles only what is owed for activity dated up to that day. |
| `groups activity <group-id>` | The group's ledger: everything that happened in it, newest first, with your share of each entry and your balance as at it. |
| `groups archive <group-id>` | Archive a group, hiding it from the active list. |
| `groups unarchive <group-id>` | Return an archived group to the active list. |
| `groups leave <group-id>` | Leave a group. Requires that your balance in it is settled. Coming back needs a link issued after you left. |
| `groups add-guest <group-id> <name>... [--link]` | Add people the group shares costs with as guests. No link unless `--link`. Answers with each guest's user id. |
| `groups guests <group-id>` | List a group's guests by user id, with whether each has a link out and until when. A link that ran out unclaimed shows as expired (`linkExpired` true, `hasLink` false, no `token`; `linkExpiresAt` says when). |
| `groups guest-link create <group-id> <user-id> [--new]` | Print the guest's link and when it expires, issuing one if there is none or the one out has expired. `--new` replaces a working one (confirmation required): the old one stops working. Replacing an expired one asks nothing. |
| `groups guest-link revoke <group-id> <user-id>` | Stop the guest's link. Confirmation required. The guest stays. |
| `groups invite <group-id> <name>...` | Deprecated: `add-guest --link`. |
| `groups invitations <group-id>` | Deprecated: lists the guests with a link out, by their old invitation id. |
| `groups withdraw-invitation <group-id> <invitation-id>` | Deprecated: stops that guest's link. The guest stays; nothing moves. |
| `groups link show <group-id>` | Show a group's standing join link. |
| `groups link create <group-id>` | Make a join link for a group. Any link the group already had stops working. |
| `groups link revoke <group-id>` | Turn off a group's join link, so the URL stops working. |

The three deprecated commands last one release, and so does `isPendingInvitee` beside
`isGuest` in the JSON.

### transactions

Expenses and settlements.

| Command | |
| --- | --- |
| `transactions list` | The expenses **you** paid for, newest first. With `--group`, the group's whole ledger. |
| `transactions show <transaction-id>` | Show one transaction and how it was split. |
| `transactions create <name> <amount>` | Record an expense. `--currency <ISO>` for a personal one in another currency (`EUR`); in a group only the group's own is accepted, so omit it there. |
| `transactions update <transaction-id>` | Change an expense or a settlement. Only what moved is sent; `--redivide` asks for a fresh division, `--preview` shows the result without changing anything. On a settlement, `--covers-through <day>` and `--no-covers-through` set or clear the period it was for. |
| `transactions summary` | Total the expenses **you** paid for. With `--group`, the group's whole ledger. One currency per total (`currency`); anything in another is in `otherCurrencies`, never added in. |
| `transactions monthly` | What **you** paid and what **your** share came to, month by month. Gross: no transfers. One currency for the whole series; others are left out. |
| `transactions shares list` | List the expenses you owe a share of, newest first. |
| `transactions shares summary` | Total the shares matching a filter. |
| `transactions bank-matches <transaction-id>` | List imported bank rows that could be this expense arriving a second time. |
| `transactions delete <transaction-id>` | Delete a transaction: an expense, or a settlement. The only command under `transactions` that takes a settlement's id — get it from `groups activity`, since the listings here read the expenses and cannot see one. An expense filed from a bank row puts the row back in the inbox, with the receipt files it had there; the confirmation says so. |

### users

The signed-in account and what it is owed.

| Command | |
| --- | --- |
| `users me` | Show the account this CLI is acting as. |
| `users position` | Show your overall balance across every group. |
| `users delete` | Delete the account you are signed in as, and its sign-in with it: the Keycloak login, any linked Google account and every session. Permanent; nobody can sign in as it again, from the CLI or the web app. Refused with `ACCOUNT_NOT_SETTLED` while any group balance is not zero. Clears the stored credentials on success. |

### settle

Squaring up with a person, across every group at once. `groups settle` and
`groups settle-up` work inside a group, because a balance belongs to one; these work on a
person, because a payment does.

| Command | |
| --- | --- |
| `settle plan` | The fewest payments that leave you square everywhere, one line per person. `--through <day>` for activity dated up to that day. |
| `settle pay <user-id>` | Record one payment between you and that person, wherever the debt between you lives. `--through <day>` settles only what that period left owing. |
| `settle history` | Every repayment you were party to, in any group, newest first. |

### categories

Expense categories and their default split rules.

| Command | |
| --- | --- |
| `categories list` | List categories. `--archived` includes the ones the group has retired. |
| `categories create <name>` | Create a category. |
| `categories update <category-id>` | Change a category's name or its default rule. `--no-rule` clears the rule, which `--rule` cannot: a flag with no value cannot say the difference between "leave it" and "clear it". |
| `categories archive <category-id>` | Stop offering a category. It leaves every listing; the expenses filed under it keep it. What people mean by deleting one they have used. |
| `categories unarchive <category-id>` | Offer a retired category again. |
| `categories delete <category-id>` | Delete a category. Refused once anything is filed under it (`CATEGORY_IN_USE`) -- archive it instead. |

### merchants

Places money gets spent, shared across every group, and their logos. A bank sync fills this
table on its own; these commands are the way in by hand, for cash at the same shop every
week or a group with no bank linked.

A merchant belongs to no group, so none of these take `--group`. You can read only your own
places: the ones an expense you can see, or a row your own bank imported, already names. A
listing returns at most 20, alphabetically, and `--search` needs at least 2 characters
(`MERCHANT_SEARCH_TOO_SHORT`). Narrow the search instead of paging. `transactionCount` is how
many of *your* transactions point at the place.

**Renaming, relogoing and deleting a merchant is refused for every account**
(`MERCHANT_EDIT_FORBIDDEN`, exit code 2). A rename would change what every group that has
spent there sees. Do not retry it, and do not sign in again over it. To fix where one expense
was spent, point that expense at another merchant: `transactions update
<transaction-id> --merchant-id <merchant-id>`, after `merchants create` if the place is new.
`merchants create` with a name that already exists hands back that merchant as it is.
`--logo-url` is accepted only on the bank provider's logo hosts
(`MERCHANT_LOGO_NOT_ALLOWED`); leave it out and the place renders as initials.

| Command | |
| --- | --- |
| `merchants list` | Your merchants, alphabetically, at most 20. `--search` (2+ characters) narrows on the name. |
| `merchants show <merchant-id>` | Show one of your merchants, with how many of your transactions point at it. |
| `merchants create <name>` | Add a place, or get back the one already called that. `--logo-url` must be a provider logo URL; without one it renders as initials. |
| `merchants update <merchant-id>` | Refused for every account (`MERCHANT_EDIT_FORBIDDEN`). |
| `merchants delete <merchant-id>` | Refused for every account (`MERCHANT_EDIT_FORBIDDEN`). |

### receipts

The itemised bill behind an expense, and dividing by who had what: the dinner where nobody
ate the same thing, where an even split is wrong and stating amounts by hand means
apportioning the tax and the tip yourself.

Every line divides by a saved split rule version, the same kind of rule a whole expense
divides by. Nobody claims a line: "Ana had the steak" is a rule naming Ana, and the line
points at it. `split-rules list --group <id>` prints the rules with their current version ids.

Transcribing a bill and dividing by it are separate commands. For a new bill, or one on an
expense whose stored shares were not produced by the bill, `set` and `rule` save the bill
without changing the shares; `preview` never stores anything, and `divide` stores the bill's
division. If the expense is already divided by its bill, a valid `set` or `rule` correction
divides it again. If a correction makes the bill incomplete, the bill stays editable and the
last valid shares stay in place until the bill is valid again.

| Command | |
| --- | --- |
| `receipts transcribe <file>` | Read a local receipt into an editable draft. Saves nothing. Supported files are JPG, PNG, WebP and PDF up to 10 MB. |
| `receipts attachments list <transaction-id>` | List source receipt files attached to an expense. |
| `receipts attachments upload <transaction-id> <file>` | Attach a source receipt file to an expense. |
| `receipts attachments download <transaction-id> <attachment-id> <destination>` | Download a source file to a new local path; refuses to overwrite an existing file. |
| `receipts attachments transcribe <transaction-id> <attachment-id>` | Read an attached source file into an editable draft. Saves no itemised bill. |
| `receipts attachments delete <transaction-id> <attachment-id>` | Delete an expense's source file. Confirmation required. |
| `receipts show <transaction-id>` | The bill, its lines with their ids, the rule each divides by, and whether it can be divided yet. |
| `receipts set <transaction-id>` | Transcribe the bill, replacing whatever was there. `--item` per line, repeatable; a new line without `@` starts on the category's split rule. `--tip`. `--subtotal`, `--tax` and `--total` default to the lines added up and the extras added on. Keeps what it has no flag for: the merchant, purchase date, the tip unless `--tip`, and, on a line named by its id, its tax and rate unless the line states `/tax` or `/notax`. Confirms first when the expense is divided by its bill. |
| `receipts rule <transaction-id> <item-id>` | Choose one line's split rule. `--rule-version <id>`; omitting it clears the line's rule. Confirms first when the expense is divided by its bill. |
| `receipts preview <transaction-id>` | What dividing by the bill would come to, per person. Stores nothing. |
| `receipts divide <transaction-id>` | Divide the expense by its bill and store the shares. Destructive: confirms with the figures. |
| `receipts delete <transaction-id>` | Take the bill off. The shares already stored are left alone. |

A line is `[<id>#]<name>=<line-total>[x<qty>][/tax<amount>][@<rule-version-id>]`, and after
the `@` comes exactly one split rule version id:

```bash
groupsplit receipts set 7c1e... --tip 6.00 \
  --item "Steak=22.00/tax1.80@<ana-only-version>" \
  --item "Wine=18.00/tax1.35@<ana-and-omar-version>" \
  --item "Beers=27.00x3/tax1.05@<omar-twice-as-much-version>"
```

The number before `x` is the total printed for the whole line, not a unit price. For
example, `Beers=27.00x3` means three beers with a line total of 27.00, or 9.00 each.

A plate the table shared is a line naming a rule that divides between them, and a bottle Omar
had twice as much of is a shares rule weighted that way. The weighting lives in the rule,
which the group can reuse and edit, not in the line.

A new line with no `@` starts on the rule the expense's category divides by, so only the
lines that divide differently need one. When the category has no rule a line can use (it
divides by the bill, or names somebody who has left), the line starts on the group's
default split, then on Even. A line already on the bill keeps the rule it has, or none if
somebody cleared it with `receipts rule`.

A line cannot name an itemised rule (a bill inside a line of a bill divides nothing) nor a
rule belonging to another group. Both are `RECEIPT_INVALID`.

`/tax8.05` says 8.05 of the bill's tax was charged on that line. It is an amount, not a rate
or a flag, because one bill can charge two rates (6% on food and 23% on household goods on a
supermarket receipt) and knowing which lines were taxed does not say how much each carried.
The amounts have to come to `--tax`. A line carries none unless it says so, which is what a
restaurant bill under VAT wants; `/notax` still reads and means the same as saying nothing.
It reads the same on either side of the `@`.

**`receipts set` saves the bill whole**, so a line you do not mention is deleted, and its rule
with it. Correcting one price means naming the stored lines by the ids `receipts show`
prints:

```bash
groupsplit receipts set 7c1e... --item "a1b2c3d4-...#Steak=24.00@<version>" --item "b2c3d4e5-...#Wine=18.00@<version>"
```

Only text before a `#` that parses as an id is read as one, so `Table #4` is still a name. A
line with no id is a new line. An id from another bill, or the same id twice, is refused.

What `set` has no flag for is kept: it reads the saved bill first and sends back the merchant,
the purchase date and the tip (unless `--tip`), and each line named by its id keeps its tax
amount, rate and taxable flag unless that line states `/tax` or `/notax`. So a corrected price
on a taxed bill needs only the price. If a correction leaves a line without a rule, the
stored shares stay as they were, and the `set` or `rule` call that gives it one divides the
expense again.

```bash
groupsplit receipts show 7c1e...            # line ids, rules, whether it divides
groupsplit receipts rule 7c1e... <line-id> --rule-version <version-id>
groupsplit receipts rule 7c1e... <line-id>  # back to no rule
groupsplit receipts preview 7c1e...         # stores nothing
groupsplit receipts divide 7c1e...          # confirms with the figures
```

Tax is per line and the tip is spread over the lines by price, so each line is divided by its
own rule as one amount: price, plus its tax, plus its share of the tip. That is one
calculation, with a single rounding and a single remainder, which goes to the largest line,
as in every other division.

What it refuses, before dividing:

| Code | |
| --- | --- |
| `RECEIPT_ITEMS_MISSING_RULE` | A line has no split rule. Carries `itemIds`. Refused rather than spread over everybody: charging five people for one person's steak is the kind of wrong nobody checks for afterwards. The bill is still stored and readable; it just will not divide. |
| `RECEIPT_DOES_NOT_ADD_UP` | The lines do not come to the subtotal, the per-line tax does not come to the bill's tax, the parts do not come to the total, or the total is not the expense's amount. Carries the figures it compared. |
| `RECEIPT_NOT_FOUND` | No bill on that expense -- or, when dividing, the expense is filed under an itemised rule and nobody has attached one. |
| `SPLIT_ON_A_PERSONAL_EXPENSE` | A personal expense has nobody to divide lines between, so it takes no bill. |
| `TRANSACTION_GROUP_LEFT` | Somebody who has left the group can still read the bill on an expense they paid for, and can no longer change or divide by it. |

A bill belongs to an expense: an imported bank row is itemised by filing it first
(`inbox file`) and typing the bill against the expense it became. One charge files as one
expense.

A source file is not the bill. Transcribing one gives a draft and saves nothing: review it,
create the expense if there is none yet, then save the bill with `receipts set`.

```bash
groupsplit receipts transcribe ./receipt.pdf  # for an expense not yet recorded
groupsplit receipts attachments upload <transaction-id> ./receipt.pdf
groupsplit receipts attachments transcribe <transaction-id> <attachment-id>
groupsplit receipts attachments download <transaction-id> <attachment-id> ./copy.pdf
```

Every group is given one "divide it by the bill" rule and cannot write a second, since the
rule holds no settings for a second to differ by. To divide by the bill by default, find it
in the listing and point a category at it:

```bash
groupsplit split-rules list --group <id>          # the given one is named "Divide by the bill"
groupsplit categories update <category-id> --rule <rule-id>
```

`split-rules create` has no `--itemized`: a second is refused with
`SPLIT_RULE_BILL_IS_PROVISIONED`, which names the one the group holds.

An expense filed under that category with a bill divides by it and records which version did
so. One with no bill is refused by name, not split evenly. An expense under any other rule
can still be divided by its bill: `receipts divide` stores the result as stated shares, the
same as typing them.

To say where an expense was spent, name the merchant on the expense rather than here:
`transactions create --merchant-id`, or `transactions update --merchant-id` to add it
afterwards and `--no-merchant` to forget it. Filing a bank row sets it from the row itself,
so an imported expense already knows.

### split-rules

Reusable rules describing how an expense is divided.

| Command | |
| --- | --- |
| `split-rules list` | List split rules. |
| `split-rules show <rule-id>` | Show one rule and the division it stands for now. |
| `split-rules versions <rule-id>` | Show every division the rule has stood for, and when each stopped. |
| `split-rules create <name>` | Create a split rule. |
| `split-rules update <rule-id>` | Rename a rule, or change how it divides from now on. |
| `split-rules versions set <rule-id> --file <history.json>` | Write the divisions a rule stood for *before* it was recorded here. `--dry-run` prints the chain and sends nothing; the real run is exit-4 gated. |
| `split-rules delete <rule-id>` | Delete a split rule. Refused once an expense has been divided by it. |

Every group also holds one rule per member that puts the whole amount on them, provisioned
rather than written. `split-rules list` has a column each for the member a rule is **all
for** and whether it is **built in**. Nothing renames, restates or deletes a built-in one
(`update` and `delete` refuse before sending, and the API's code is `SPLIT_RULE_NOT_EDITABLE`):
an expense may name one without anybody having created it, so what it says has to stay
fixed. A group that wants a division of its own in that shape creates one with
`--sole <user-id>`, and that one is editable like any other.

```bash
groupsplit split-rules list --group <group-id>  # "All for" and "Built in"
groupsplit split-rules create "Ana's gym" --group <group-id> --sole <user-id>
groupsplit split-rules create "Mine" --group <group-id> --payer
```

No rule changes kind: `update` with a different shape is refused (`SPLIT_RULE_KIND_FIXED`).
Change the numbers, or create a rule for the new shape. Writing a history is exempt, since it
records a past that did change shape.

A rule is a name with a history behind it. Editing how it divides starts a new version and
closes the one before it; renaming it starts none. Every expense records the version that
divided it, so editing a rule changes what the *next* expense is pre-filled with and nothing
already recorded. Editing an old expense's amount re-divides it by the version it was written
under, not by what the rule says today. Filing it under another category is what moves it
onto a different rule.

### invitations

Guest links and group join links you were sent.

| Command | |
| --- | --- |
| `invitations list` | The guest links you have opened that still work, with their tokens and when each expires. Not "sent to me" -- there is no address to match. |
| `invitations show <link>` | Show what a guest's link leads to, and until when it works, without claiming it. Says nothing about the money. |
| `invitations claim <link>` | Claim it: join the group in the guest's place, taking on the shares recorded for them, their places in the group's split rules and their "all for them" rule. Repayments recorded between you and that guest are deleted (they would be self-payments) and counted. If two people claim one link at once, one gets in and the other gets GROUP_INVITATION_NOT_FOUND. Confirmation required. |
| `invitations decline <link>` | "This isn't me": the link stops working. The group keeps the guest; nothing moves. Confirmation required. |
| `invitations link <link>` | Show which group a *join* link leads to, without joining. |
| `invitations join <link>` | Join the group a join link leads to, as yourself. |

`list` is the only command here that takes no link. It returns every guest link this account
has opened that still works, so it is the way back to one when the user no longer has the
message. The others take the link, as the whole URL or the token inside it.

```bash
groupsplit invitations list            # guest links you have opened that still work
groupsplit invitations show <link>     # what it leads to, claiming nothing
groupsplit invitations claim <link>    # take the guest's place; asks first
groupsplit invitations decline <link>  # "this isn't me": the link stops working
```

### bank

Linked banks: what is connected, syncing it, and unlinking.

A connection's status is `active`, `login required`, `revoked`, or one of two that still
sync, `account not shared` and `sign-in expiring`. `login required` means the bank needs the
person: a new sign-in, a locked account, a password reset, something to finish on the bank's
own site, or no account shared. Each ends in signing in again through update mode.
`account not shared` means the bank has an account this connection is not importing; sign in
again with it ticked, then `bank refresh`. `sign-in expiring` works, but not for much longer.
An account's `Access` reads `withdrawn` when that one account was revoked at the bank and the
rest of the connection is fine.

Linking happens in the provider's own UI, in a browser; the CLI does the token before it and
the exchange after. `bank sync` answers once the pull is queued, not when it lands, so poll
`inbox summary`, which is one count rather than a page. A server with no provider configured
says so from `bank list` rather than listing nothing.

```bash
groupsplit bank link-token                                # for the provider's UI
groupsplit bank link <public-token>                       # exchange what it gave back
groupsplit bank link-token --connection <connection-id>   # update mode: sign in again
groupsplit bank refresh <connection-id>                   # after sharing another account
groupsplit bank sync <connection-id>                      # queued, not awaited
groupsplit inbox summary                                  # cheap to poll
```

| Command | |
| --- | --- |
| `bank list` | List the banks you have linked, and their accounts. |
| `bank link-token` | Ask for a token that opens the provider's linking UI in a browser. `--connection <id>` repairs an existing connection rather than linking a new bank. |
| `bank link <public-token>` | Finish linking a bank with the token its UI returned. |
| `bank refresh <connection-id>` | Re-read the bank's accounts and queue a sync. Run after an update-mode sign-in that shared another account: nothing else tells the app about it. |
| `bank sync <connection-id>` | Ask for a fresh pull of transactions from a bank. |
| `bank unlink <connection-id>` | Unlink a bank and stop syncing it. Receipt files still waiting on its inbox rows are deleted; expenses keep theirs. Succeeds when the provider has already let go of the item. |

### inbox

Imported bank rows waiting to be filed.

| Command | |
| --- | --- |
| `inbox list` | List imported rows, newest first. |
| `inbox summary` | Count the rows still waiting to be filed, and how many of those may already be recorded. |
| `inbox matches <row-id>` | List the expenses already recorded that an imported row could be. |
| `inbox attachments list <row-id>` | List source receipt files attached to a pending bank row. |
| `inbox attachments upload <row-id> <file>` | Attach a source receipt file while the row is still pending. |
| `inbox attachments download <row-id> <attachment-id> <destination>` | Download a pending source file to a new local path. |
| `inbox attachments transcribe <row-id> <attachment-id>` | Read a pending source file into an editable draft. Saves no itemised bill or expense. |
| `inbox attachments delete <row-id> <attachment-id>` | Delete a pending row's source file. Confirmation required. |
| `inbox file <row-id>` | File an imported row as an expense. |
| `inbox link <row-id> <transaction-id>` | Attach an imported row to an expense already recorded, instead of filing a second one. |
| `inbox dismiss-match <row-id> <transaction-id>` | Say an imported row and a suggested expense are not the same money. |
| `inbox ignore <row-id>` | Keep a row out of the inbox without filing it. |
| `inbox restore <row-id>` | Put an ignored row back in the inbox. |

Filing rows without recording the same money twice, and what to do about a
`POSSIBLE_DUPLICATE_EXPENSE` refusal, is the groupsplit-inbox skill.

```bash
groupsplit inbox file <row-id> --group <group-id> --category-id <category-id>
groupsplit inbox ignore <row-id>                           # undone by inbox restore
groupsplit inbox link <row-id> <transaction-id>            # it is the same payment
groupsplit inbox dismiss-match <row-id> <transaction-id>   # not a match at all
```

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

Four ways to divide, as flags. **Give exactly one.** Passing two is refused, not resolved by
precedence, because a caller who passed both had one of them in mind.

| Flag | Divides |
| --- | --- |
| `--even` | Equally. With no `--among`, between whoever is in the group at the time. |
| `--even --among <user-id> <user-id>` | Equally, but only between the members named. Repeatable. |
| `--sole <user-id>` | Not at all: all of it is for that member, whoever paid. |
| `--payer` | The same, naming **you**: the CLI reads your own id so you do not have to pass it. |
| `--percent <user-id>=<percent>` | By percentage. Repeatable, decimals allowed: `--percent 8f0c...=33.33`. |
| `--shares <user-id>=<shares>` | By whole shares. Repeatable: `--shares 8f0c...=2`. |

`--payer` once meant "whoever paid owes all of it", a rule that named nobody and changed
meaning when the payer was corrected. That rule is gone: `--payer` now names you, and every
rule of this kind names a member.

A category carries a rule, and the rule divides an expense filed under it. The usual order is
to create the rule, create the category pointing at it, then file expenses under the
category.

```bash
groupsplit split-rules create "Two thirds me" --group $G --percent $ME=66.67 --percent $YOU=33.33 --json
groupsplit categories create "Rent" --group $G --rule $RULE_ID --json
```

An expense under no category, or under one naming no rule, divides by the group's default.
Every group is given an "Even" rule and starts pointed at it; `groups show` names it under
"Default split", and `groups set-default-split` moves it, though not onto the group's "divide
it by the bill" rule (`SPLIT_RULE_INVALID`). Moving it changes the next expense and nothing
recorded. An expense recorded before the group had a default holds no version, so its first
edit adopts the default as it is then: on a back catalogue, move the default before editing.

```bash
groupsplit groups show $G                                  # "Default split", with its id
groupsplit groups set-default-split $G $RULE_ID
```

`update` takes the same flags and means "from now on". To answer *how was this expense
divided*, read `split-rules versions <rule-id>`, not `show`: `show` is what the rule says
today, and an expense from March may have been divided by an earlier entry.

#### Writing a rule's past: `versions set`

Only for a rule imported with no history -- one that has stood for a single division since it
was made. The file is a JSON array, oldest first, of `{"from", "definition"}` where the
definition is the same `$type` shape `split-rules show` prints:

```json
[
  { "from": "2023-03-01", "definition": { "$type": "shares", "shares": { "<user-id>": 3 } } },
  { "from": "2026-01-01", "definition": { "$type": "even" } }
]
```

A `from` with no offset is midnight UTC. Each entry runs until the next starts, so the dates
must strictly increase, no entry may start in the future, and the **last entry has to be how
the rule divides today**: it is the row every recorded expense already points at, and it is
reused, not replaced. The refusals are `SPLIT_RULE_ALREADY_HAS_HISTORY`,
`SPLIT_RULE_HISTORY_ENDS_ELSEWHERE` and `SPLIT_RULE_HISTORY_INVALID`.

```bash
groupsplit split-rules versions <rule-id>
groupsplit split-rules versions set <rule-id> --file history.json --dry-run  # print the chain
groupsplit split-rules versions set <rule-id> --file history.json            # exit 4
```

Writing the history changes no expense: each keeps the version it already records, and the
open one is backdated, not replaced. To say which version divided a particular expense, use
`transactions update <id> --divided-by <version-id>` or `--hand-split` (the amounts are the
expense's own). Those two contradict each other and contradict `--split` and `--redivide`.
There is no pass that does it for a whole group: guessing each expense's rule from its
category's *current* pointer is wrong for every group that has re-pointed a category since.

```bash
groupsplit tx update <id> --hand-split               # these amounts are the expense's own
groupsplit tx update <id> --divided-by <version-id>  # this version of a rule worked them out
```

Neither moves a penny; they decide what a later edit does. An expense recorded as a rule's
follows the rule when its amount or payer changes. One recorded as its own refuses an amount
edit, unless its shares happen to be an even division, which the app made and would make
again.

Either one alongside an ordinary edit is two requests, the edit and then the record of what
divided it, so a failure says which half landed ("the edit was saved, but recording what
divided it was not"). Run the same command again: it reads the expense first and sends only
what still differs, so nothing is applied twice.

### One expense that is one person's: `--split-rule`

An expense that divides unlike its neighbours can name the rule it divides by, whatever its
category says. The ids come from `split-rules list --group <group-id>`, where the **all for**
column is the member each rule puts the whole amount on:

```bash
groupsplit tx create "The gym" 60 --group $G --split-rule $RULE --json
groupsplit tx update <id> --split-rule $RULE      # this one was not ours, it was Ana's
groupsplit tx update <id> --redivide              # back to what its category says
groupsplit inbox file <row-id> --group $G --split-rule $RULE
```

Prefer this to typing the whole amount against one person with `--split`: a named rule keeps
the expense divisible, so correcting the amount afterwards divides it again, while hand-typed
shares are refused because they no longer sum to the new total. Passing both `--split-rule`
and `--split` is refused before anything is sent.

`--split-rule` is an instruction, not a stored field: an expense stores the division it was
written under, not the rule somebody picked to get it. Leaving it out keeps the expense
dividing as it does, and `--redivide` is the way back to its category. Stating shares gives
it up too, as stating shares always gives up the version behind an expense.

### Setting exact shares: `--split`

`transactions create`, `transactions update` and `inbox file` take `--split <user-id>=<amount>`:
money, not percentages or share counts, repeatable, and they set the division outright.

On `transactions update`, omitting the flag **never restates a division somebody typed**
(since 2026-09-09; a bulk pass that assumed otherwise re-divided 733 expenses). What it does
with a division *nobody* typed depends on what the edit touches:

| The expense's shares were | You change | What happens |
| --- | --- | --- |
| typed by somebody | anything but the amount | kept exactly as they are |
| typed by somebody | the amount | **refused** (`SPLITS_DO_NOT_SUM_TO_AMOUNT`): those shares no longer add up |
| worked out by a rule (or evenly, under none) | the name, note, date or merchant | kept exactly as they are |
| worked out by a rule (or evenly, under none) | the amount, payer, category or group | worked out again at the new values |
| worked out by its itemised bill | the amount | the bill is re-totalled, the difference on its tip, and the expense divided by it again; lowering it by more than the tip is **refused** (`RECEIPT_DOES_NOT_ADD_UP`) |
| worked out by its itemised bill | the group | every bill line's rule is cleared (they are the old group's); give them the new group's with `receipts rule` |

Two consequences:

- Changing the amount on its own is **refused** (`SPLITS_DO_NOT_SUM_TO_AMOUNT`) on an expense
  somebody split by hand, because the old shares do not add up to the new total. Either pass
  `--split` for every member alongside `--amount` (read the current shares from
  `transactions show` first), or pass `--redivide`. On an expense a rule divided it works,
  including one divided by its bill, whose tip takes the difference.
- Changing the category re-divides an expense a rule divided, by the new category's rule, and
  leaves a hand-typed division alone.

`--redivide` discards the shares the expense holds and asks its category's rule to divide it
again, **by the version the expense records**, not by the rule as it reads today. 90.00
recorded under a two-to-one rule comes back 60.00 / 30.00 even after that rule has been
changed to one-to-one. To reach a rule as it stands now, file the expense under a different
category (`--category-id`): the version it holds belongs to a rule it is then no longer filed
under, so the new category's current rule decides. An expense somebody split by hand records
no version (stating shares gave up the one it had), so `--redivide` there divides by the rule
as it reads now.

`--split` and `--redivide` contradict each other. Prefer `--split` when the user gave you
per-person figures and `--redivide` when they said "just split it the usual way".

```bash
groupsplit tx update <id> --amount 120.00 --split $ME=80 --split $YOU=40  # these amounts
groupsplit tx update <id> --amount 120.00 --redivide                      # the rule decides
groupsplit tx update <id> --amount 58.00 --preview                        # bill 48, tip 10
```

### Changing an expense

`transactions update` sends only what you name; everything else is left alone. The pairs that
mean "unset" are separate flags, because a flag with no value cannot say the difference
between "leave it" and "clear it":

| To | Pass |
| --- | --- |
| Move it to a group | `--group <group-id>` |
| Take it out of its group, back onto your own ledger | `--personal` |
| File it under a category | `--category-id <category-id>` |
| File it under nothing, so the group's default divides it | `--no-category` |

A command that names nothing is refused (exit code 3), since the server would take an empty
patch as a successful no-op. The command reads the expense first and sends the difference, so
setting a field to the value it holds succeeds and changes nothing, and the same command can
be run twice. These pairs are refused before anything is sent: `--group` and `--personal`,
`--category-id` and `--no-category`, `--merchant-id` and `--no-merchant`, and `--split-rule`
with `--split` or `--redivide`.

```bash
groupsplit tx update <id> --name "Dinner at Omar's"    # shares kept, whatever divided them
groupsplit tx update <id> --description ""             # clear the note
groupsplit tx update <id> --category-id <category-id>  # a rule's division follows the category
groupsplit tx update <id> --no-category                # back to the group's default
```

### The personal group and other currencies

Every account has a personal group, listed first by `groups list`, whose id is the user's own.
An expense with no `--group` (`transactions create`, `inbox file`) goes there, and
`transactions update --personal` moves one there. It divides nothing: `--split` on a personal
expense is `SPLIT_ON_A_PERSONAL_EXPENSE`, and anything that needs a second person is
`PERSONAL_GROUP_NOT_SHARED`. Its balance is always zero, so `users position` and
`settle plan` leave it out.

A category belongs to one group, so moving an expense filed under one (`--personal` or
`--group`) is refused with `CATEGORY_NOT_FOUND` unless the same command passes
`--no-category` or a `--category-id` from the group it is moving to.

```bash
groupsplit categories create "Groceries" --group <your-user-id>   # a category of your own
groupsplit tx create "Weekly shop" 64.20 --category-id <category-id>
groupsplit tx update <id> --personal --no-category
groupsplit tx list --group <your-user-id>                         # your own spending
groupsplit tx create "Dinner in Lisbon" 20 --currency EUR         # personal, in euros
```

A personal expense may be in any ISO 4217 currency; a group takes only its own, so leave
`--currency` off there. An expense keeps its currency for good: no flag changes it, since that
would relabel the amount without converting it. Money crossing into a group in another
currency is `CURRENCY_MISMATCH` on `transactions create --group`, `inbox file --group` and
`transactions update --group`, and nothing is written. Text output names a currency only when
it is not the default (`20.00 EUR`); `transactions show` always does, and JSON always carries
`currency`. `transactions monthly` draws one currency and says under the table what it left
out.

### Before writing anything: `--preview`

`transactions create ... --preview` shows the split the server would apply and creates
nothing. Use it whenever the division matters, and show the result before committing.

`transactions update ... --preview` does the same for an edit, and changes nothing:

```bash
groupsplit tx update <id> --amount 120.00 --preview --json
```

It reproduces the save, refusals included, so an amount changed on its own comes back refused
here, before anything is saved. That is the moment to add `--split`.

The rule it names is the version that divided this expense, which may not be the rule as it
reads now; when the rule has been edited since, the output says so and gives the date it
stopped being current. `--preview` on a settlement is refused: a settlement is one payment to
one person and has no division to show.

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
`--owed-only`, which keeps only the rows that are a debt: your share of an expense you paid
for yourself is not money you owe.

A `shares list` row carries `amount`, what the whole expense came to, and `share`, your part
of it. An expense you paid for and owe a share of is listed too (`paidByYou`). `shares summary`
answers `share`, your part of everything, and beside it `owedToOthers`, the part on expenses
somebody else paid; only that one is a debt.

```bash
groupsplit tx shares list --sort-by share                   # what is costing you the most
groupsplit tx shares list --group <group-id> --from 2026-01-01
groupsplit tx shares summary
```

`transactions list` and `transactions summary` add `--paid-by <user-id>`, which narrows to
one person's spending. Ids come from `groups members <group-id>`.

#### `--group` does two different things

One name covers both behaviours, so read the flag's description in the schema for each
command:

| Command | `--group` means |
| --- | --- |
| `transactions list`, `transactions summary` | **the group's whole ledger** -- every expense in it, whoever paid |
| `transactions monthly`, `transactions shares list\|summary` | only *your* rows in that group |

```bash
groupsplit tx list                                          # expenses you paid for
groupsplit tx summary                                       # and their total
groupsplit tx list --group <group-id>                       # the group's whole ledger
groupsplit tx summary --group <group-id>                    # and its total
groupsplit tx list --group <group-id> --paid-by <user-id>   # one person's spending in it
```

Without `--group`, the first two answer only the caller's own expenses: a group of 1,411
expenses answers a count of 2 to a member who paid for two of them. A short answer from these
is not evidence a group is empty; check which question you asked.

The two go together. If you list with `--group`, total with `--group`, or the page and the
figure beside it describe different sets.

A group the caller is not in -- including one they have **left** -- is refused with exit
code 3 and `GROUP_NOT_FOUND`, not answered with an empty list. Their own expenses in a group
they left are still in `transactions list`; the group's ledger is the group's.

`groups activity` takes `--from`, `--to`, `--search` and `--kind` (`Expense` or `Transfer`).
It ignores the group's default period (`set-default-period`): without `--from`/`--to` it is
all time, whatever the app opens on. Without `--kind` it lists both kinds. `--from`/`--to`
are date-times there and calendar dates (`2026-01-01`) on `inbox list`, which is what a bank
puts on a row.

```bash
groupsplit groups set-default-period <group-id> all-time  # the app's Activity tab only
```

`transactions summary` and `transactions shares summary` take the same filters and answer
with totals instead of rows. Ask them instead of paging a list and adding it up yourself.

### The two ways in to a group

They are different doors and are not interchangeable.

A **join link** is the group's open door: `groups link create` mints one, `groups link show`
reads it back, `groups link revoke` turns it off. It is reusable, it expires, and it lets
whoever follows it in **as themselves**, claiming nothing. On the receiving side,
`invitations link <link>` says which group it leads to without joining, and
`invitations join <link>` joins.

A **guest's link** comes from `groups guest-link create <group-id> <user-id>`, one per
guest. It hands over a *position in the group's ledger*: the shares recorded for that guest
become the claimer's. So it works once and for 30 days from being issued, it says nothing
about the money until claimed, and `invitations claim <link>` is gated by the confirmation
protocol. Never suggest forwarding one, and never paste one where the group's join link would
do.

### Guests

`groups add-guest <group-id> <name>...` names people the group is sharing costs with who have
no account in it. The group can point at them at once, so **use their id the way you would a
member's**: give them a share with `--split`, name them with `--paid-by`, settle with them.
Do not wait for anybody to sign up, and do not leave them out of a division the user
described as including them.

```bash
groupsplit groups add-guest <group-id> Carlos Nuria  # answers with their user ids
groupsplit tx create Rent 90 --group <group-id> --split <anabel-id>=60 --split <carlos-id>=30
groupsplit tx create Taxi 40 --group <group-id> --paid-by <carlos-id>
```

Their id is the user id, from `groups add-guest`, `groups guests`, or the `groups members`
row with `isGuest` true. It is the one id for splits, payer, repayments, their link and
removing them. The token is not an id; it is the link to send them.

They also have the built-in rule every member has, putting the whole amount on them: the
`split-rules list` row whose "All for" is their id. `--split-rule` with it records "this one
is all theirs", and the rule becomes the claimer's if somebody claims their link.

**You record their side.** A guest cannot sign in, so "Carlos paid me back 30" is
`groups settle <group-id> <carlos-id> 30`, "I paid Carlos" adds `--direction youpaidthem`,
and "Carlos paid Nuria" is `groups settle-between <group-id> <carlos-id> <nuria-id> <amount>`.
`groups settle-up` and `settle pay` include guests like anybody else (`groups balances` and
`settle plan` mark them `(guest)`).

```bash
groupsplit groups settle <group-id> <carlos-id> 30                          # Carlos paid you
groupsplit groups settle <group-id> <carlos-id> 30 --direction YouPaidThem  # you paid Carlos
groupsplit groups settle-between <group-id> <carlos-id> <nuria-id> 15       # Carlos paid Nuria
groupsplit settle pay <carlos-id>                                           # across groups
```

**Removing one** is `groups remove-member <group-id> <user-id>`, refused with
`GROUP_MEMBER_NOT_SETTLED` (and their balance) until they are square, so record the repayment
first. Afterwards they are a past guest: their old shares are fixed, nobody can name them for
new money, and the group's join link is untouched.

**A link is optional.** Issue one only when the user wants the real person to take over:
`groups guest-link create` prints the link (safe to repeat: it returns the one already out),
`--new` replaces it, `revoke` stops it. Declining a link only stops it; the
guest stays.

```bash
groupsplit groups guest-link create <group-id> <carlos-id>        # issue, or print the one out
groupsplit groups guest-link create <group-id> <carlos-id> --new  # replace it; asks first
groupsplit groups guest-link revoke <group-id> <carlos-id>        # stop it; the guest stays
groupsplit groups add-guest <group-id> Carlos --link              # add and issue in one go
```

Nobody is notified: sending the link is the group's job. Text output prints it as
`<server>/claim/<token>` when a server origin is configured; JSON carries the `token`.
Replacing a link also forgets who had opened it, so a link that leaked is not handed on.

**A link expires 30 days after it was issued**, claimed or not. An expired link answers
`GROUP_INVITATION_NOT_FOUND`, the same 404 as a claimed or mistyped one, and `groups guests`
shows it as expired. `groups guest-link create` (no `--new` needed) issues a fresh one, which
also counts as issued now for the rejoin rule. Tell the user the old URL is dead and the new
one has to be sent again.

**If the user has lost their own link**, `invitations list` has it -- provided they opened
it at least once. If they never did, a member can print it again with `groups guest-link
create`.

**The tokens are credentials.** A guest's link is a claim on a position in the group's
ledger: whoever opens it takes on the shares recorded for that guest. Hand one to the user
who asked for it and nowhere else -- never into a shared channel, a commit, an issue, or a
transcript you did not have to write it into. Say "send Carlos his link" rather than pasting
it unasked.

A claim moves money onto the claimer's account and answers with what moved, including any
repayments that cancelled out. **Report that output**; somebody's balance changed.

### Settling up

`groups balances <group-id>` says who owes whom. `groups settle <group-id> <user-id>
<amount>` records a repayment; `--direction` defaults to `theypaidyou`, the creditor's side,
so pass `--direction youpaidthem` when the user is the one who handed over the money. It is
confirmation-gated: it moves money in the ledger.

`groups settle-up <group-id>` records all of them in one go: everything the user owes and is
owed in that group, each repayment the same as `groups settle` would write for that pair. It
settles the user's own position and takes no amounts; `--date` and `--note` go onto every
repayment it writes. It reads the balances first, so the confirmation lists the repayments
themselves, and `--dry-run` shows that list without writing anything. Being already square
is success, not a refusal: it says so and writes nothing.

`--through <day>` narrows it to a period: "settle August" is `--through 2026-08-31`. What is
owed through a day counts the expenses dated up to the end of it and the repayments made for
it. **Repayments made for later count only as far as they pay down what the period left**,
because money paid goes against the oldest debt first. So a payment made in September for
August is never asked for twice, one made for September never reads as owed back for August,
and settling a period that is already square says so and writes nothing. Expenses after the
day never move the figure, except that once a later period has been settled a period never
asks for more than that one leaves owing. Do not work out a month's figure by filtering
expenses and subtracting; that leaves out repayments. The repayments it records remember the
day, and `transactions show <settlement-id>` reports the period they covered and anything
still owed for it, including an expense dated inside the period and added afterwards.
`settle history` shows the day on the row.

The day ends at midnight on the machine's calendar: the CLI sends its current UTC offset
beside it, as the web app does. A settlement's period can be corrected or cleared afterwards.
An expense has none, and `--covers-through` on one is refused before anything is sent.

```bash
groupsplit groups balances <group-id> --through 2026-08-31
groupsplit groups settle-up <group-id> --through 2026-08-31 --dry-run
groupsplit transactions update <settlement-id> --covers-through 2026-08-31
groupsplit transactions update <settlement-id> --no-covers-through
```

It never records money moving between two other members. The user can say what they paid and
what they were paid, because they were there for both; a payment between two other people is
not theirs to state.

### Settling with a person, across groups

A balance belongs to a group; a payment belongs to a person. The `settle` commands let
somebody who owes the same friend in two groups settle once.

`settle plan` adds the per-group minimisation up by person: one line each, largest first,
naming the groups its figure comes from so the arithmetic is checkable on the row. The three
figures over the top are the same ones `users position` leads with.

`settle pay <user-id>` records one payment. `--amount` defaults to everything outstanding
between the two of them or, with `--through <day>`, to what that period left owing between
them, which also decides the groups it lands in. `--direction` works as it does on
`groups settle` and defaults to `theypaidyou`. The API writes one transfer per group in a
single save, spending the amount over the groups largest first. That order is not a flag;
do not present it as a choice. Which group a payment lands in is bookkeeping the person
handing over the money does not have to do.

It reads the plan first, so the exit-4 confirmation lists the groups the payment will land
in and what each takes. Show those lines; the user cannot see them anywhere else.
`--dry-run` prints the same breakdown and writes nothing.

```bash
groupsplit settle plan --json
groupsplit settle plan --through 2026-08-31 --json
groupsplit settle pay <user-id> --amount 70 --direction YouPaidThem --dry-run
groupsplit settle pay <user-id> --through 2026-08-31 --dry-run
groupsplit settle history --json
```

Being square with somebody is success, not a refusal: the command says
`"status": "nothing-outstanding"` and writes nothing. So is asking in a direction with
nothing outstanding in it.

`settle history` answers "did I already pay this?" across groups, because a payment can span
them.

A settlement recorded wrongly is taken back with `transactions delete <transaction-id>`,
which balances the ledger back to what it read before. Its id comes from `groups activity`
or `settle history`: the `transactions` listings read the expenses, so a settlement does not
appear in `transactions list`. `transactions show <transaction-id>` does describe one given
its id, including the period it covered when it was settled through a day.

Because settlements are missing from them, no total from `transactions summary`,
`transactions monthly` or `transactions shares summary` is net, with `--group` or without.
Do not present one as what somebody is owed or owes. `users position` is that answer;
`groups balances <group-id>` is it for one group; `groups activity <group-id> --kind
Transfer` is the transfers themselves.

`groups leave` requires that the user's balance in the group is settled, so expect a refusal
until the balances are square.
