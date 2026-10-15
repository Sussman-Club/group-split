# Guests: people a group shares costs with who have no account in it

Most of the people a group splits with never sign up. The friend on the trip, the flatmate's
partner, the colleague at the dinner: the group wants their share on the ledger, and wants to
know when they have paid it back, and none of that should wait for them to make an account.

A **guest** is that person. A member adds them by name, and from that moment they are a
participant in the group's money: they can be given a share, be the payer, be settled with,
and, once square, be removed. The members record all of it. A guest can also be given a
link, so the real person can take their place; most never are.

This document is about what that means and, mostly, about the arithmetic it must not break.
The table of who may do what, by standing, is [participants.md](participants.md).

## A guest is a row with a stand-in behind it

`GroupGuest` (the table that used to be `GroupInvitation`, renamed in place with its ids)
carries the name the group gave them, who added them and when, and a required
`ParticipantUserId`: the `User` every share recorded for that person belongs to. It is always
a **stand-in** (`UserKind.StandIn`), never an existing account: a row with the name on it,
no address, and no `UserIdentity`, so nobody can sign in as it. There is no
address to recognise an account by, and guessing from a name would hand a stranger's ledger
to whoever the group happened to call Dani.

One stand-in per guest, ever (a unique index), and it is never deleted. Removing a guest
leaves it holding their history; claiming one moves everything it held to the claimer and
leaves it empty. Either way the ledger, the rule history and the membership history go on
naming it, which is the whole reason a stand-in is a user row rather than a nullable column
on `TransactionSplit`.

**One id per guest**, and it is the stand-in's user id. Splits, payers, repayments, the
guest routes (`/groups/{id}/guests/{userId}/...`) and removal all take it. The old invitation
id is still the row's own id and still answers the deprecated `/groups/{id}/invitations`
routes, and that is all it is for.

## Ended, never deleted

A guest stops being one by being **removed** (by a member, once square) or **claimed** (by
whoever held their link). Either way the row ends (`EndedAt`, `EndReason`, `EndedByUserId`)
rather than going, the same choice a departure makes on `GroupMembership`. A query filter
keeps ended rows out of every read, so `Group.Guests` and `Set<GroupGuest>()` mean current
guests everywhere without each read having to say so.

That one filter is most of the behaviour of a removed guest. They are no longer in
`IGroupParticipants.Of`, so:

- `StandingOf` answers **Departed** (from the ledger), as for a member who left;
- they cannot be named as a payer, a share or in a rule (`TRANSACTION_PAYER_NOT_IN_GROUP`,
  `SPLIT_USER_NOT_IN_GROUP`, `RULE_USERS_NOT_IN_GROUP`) or in a repayment (`USER_NOT_FOUND`);
- what they paid or owed on an old expense is fixed (`TRANSACTION_MOVES_DEPARTED_BALANCE`);
- they show among the group's past participants, marked `isGuest` -- "(removed guest)" in the
  app.

A claimed guest's stand-in holds nothing, so it is nobody: a **Stranger**, and left out of the
past participants (the history says it was claimed).

Every add, removal and claim is a `GroupMembershipEvent` (`GuestAdded`, `GuestRemoved`,
`GuestClaimed`), written by the context in the same save, so the group can still say who a
guest was, who added them, and what became of them.

## The two invariants

Everything else here is a consequence of these.

1. **Every transaction's shares sum to its amount.** Stored once, when the transaction is
   written or edited; see `TransactionSplit`.
2. **A group's balances sum to zero**, which follows from (1) as long as every share belongs
   to somebody in the listing -- or to somebody who left square and whose part nothing has
   moved since, which `TRANSACTION_MOVES_DEPARTED_BALANCE` guarantees.

That second clause is why guests are in the group's balances (`GroupNetBalance.IsGuest`
marks them), and why a guest can only be removed square: a removed guest is a departed
participant, and a departed participant has to have left at zero for the column of the
people still there to add up.

And it is why guests are in the settlement plan. A plan is a list of payments somebody
can record, and a member can record a guest's: "Pat paid me back", "I paid Pat", "Pat paid
Sam". `DebtCalculationService` minimises over everybody, so the plan clears the whole group,
and the cross-group plan (`/me/settle`) names guests too -- marked, and once per group, since
the same real person can be a guest in two groups and those are two stand-ins.

## Settling with a guest

Every route that writes a repayment looks its people up among the group's participants rather
than its members:

- `POST /groups/{id}/settle` -- the caller and a guest, either direction;
- `POST /groups/{id}/repayments` -- any two participants, guest↔guest
  included, which only a member can see happen and write down;
- `POST /groups/{id}/settle-up` and `/me/settle` -- whatever the plan says, guests included;
- editing a transfer -- either end may be a guest.

`SETTLEMENT_WITH_SELF` still refuses a repayment to oneself, and somebody who has gone is not
there to settle with (`USER_NOT_FOUND`).

## Removing a guest

`DELETE /groups/{id}/members/{userId}` serves both standings. For a guest it is refused with
`GROUP_MEMBER_NOT_SETTLED` (and the `balance`) until they are square; then the row ends as
`Removed`, their link stops, and the rules that named them stop naming them from their next
version on -- the same pruning a member who leaves gets (`RuleHandling.Prune`). Their own
"all for them" rule stays, naming the stand-in, as a leaver's does.

It does not revoke the group's join link. Removing a member does, because they could read
it and pass it on; a guest never could.

## The link

A guest has no link until a member issues one (`POST /groups/{id}/guests/{userId}/link`, or
`IssueLinks` when adding). `GroupGuest.Token` is 24 bytes from the cryptographic generator,
Base64Url, like `GroupJoinLink.Token`, and it guards much more.

A **join link** lets somebody into a group as themselves, claiming nothing. It is reusable
and it expires. A **guest's link** hands over a position in the group's ledger: forward it and
somebody else inherits the debts recorded for that guest. So:

- **Issuing is idempotent.** Asking again returns the link already out, so "send link" can
  always just copy. `Replace` mints a new one and kills the old.
- **Single use.** Claiming ends the guest and clears the token, so a forwarded copy gets the
  same 404 a mistyped link does.
- **It expires**, 30 days after it was issued (`GroupGuest.LinkLifetime`), so an unclaimed
  link in an old chat, a screenshot or a browser history stops being a way to take somebody's
  place. See [Expiry](#expiry).
- **Every way a link ends forgets who opened it.** Replaced, stopped, turned down, claimed, or
  the guest removed: `GuestLinkExtensions.EndLink` clears the token *and* deletes the
  `InvitationOpened` rows for that guest. (Expiring is not an ending: nothing runs at that
  moment, so an expired link's rows go when a member issues the next one.) Otherwise
  somebody who opened a leaked link would be handed its replacement in their own list.
- **It is a concurrency token**, and so is `EndedAt`. Every write that matters to a guest
  changes one of them, so of two at the same moment the second matches no row: two claimers
  end with one member and one 404; a claim racing a replacement, a stop or a removal resolves
  to exactly one of them. On Postgres the loser's update waits on the winner's lock, re-reads
  the row, finds the token gone and updates nothing -- and has written nothing to undo.
- **Not a way back for somebody who left after it was issued.** The rejoin rule reads
  `LinkIssuedAt`, not when the guest was added: a guest added in March whose link was issued
  after somebody left in May lets them back.
- **It says nothing about the money.** `GET /invitations/claims/{token}` answers the group's
  name, its size, who sent the link and which guest it was made out for.
- **It lives where the ledger lives.** Tokens come back only on routes scoped to the group's
  own members.

The web app copies the link rather than printing it on the page: a URL on screen is a URL in
a screenshot.

### Expiry

A guest's link works for `GroupGuest.LinkLifetime` (30 days) from `LinkIssuedAt`, fixed
in code like `GroupJoinLink.Lifetime` (14 days) and for the same reason: a choice on a dialog
nobody has an opinion about, against a link that is one press to issue again. Longer than a
join link's because the person a guest's link is for has usually never heard of the app, and
"I'll sign up when I get home" takes a while.

- **Worked out, not stored.** There is no expiry column: the end is always `LinkIssuedAt` plus
  the lifetime, and a second column could only disagree with it. Changing the lifetime
  changes every link already out, which is what a change made for safety should do. Links
  issued before this existed expire 30 days after they were issued, with no backfill.
- **The same 404.** An expired link answers `GROUP_INVITATION_NOT_FOUND`, as a claimed,
  stopped or mistyped one does. A join link says it expired (`GROUP_JOIN_LINK_EXPIRED`), but a
  join link hands over nothing; saying "expired" here would confirm to whoever holds a leaked
  link that a position was once on offer. The claim page names expiry among the reasons a
  link stops working and says to ask the group for a new one.
- **Checked first.** `Held` refuses an expired link before anything else about the caller, the
  rejoin rule included, so somebody who left after it was issued gets the 404 too rather than
  being told the link predates their leaving.
- **Judged once, when the claim arrives.** `Claim` reads the clock once and judges the link by
  that instant throughout, re-read included. A claim that arrives a moment before the expiry
  completes even if it finishes after; one that arrives at or after it is refused before
  anything is written. Reading the clock twice could pass the first check and fail the
  second, after the work had begun.
- **Shown to the group.** `GET /groups/{id}/guests` reports `LinkExpiresAt`, and for a link
  that ran out `LinkExpired` true, `HasLink` false and no `Token`: there is nothing to copy.
  The web app's guest row says "link expired 7 Nov 2026" and offers "Send link" again; the CLI
  marks it expired. The deprecated `GET /groups/{id}/invitations` and `GET /invitations` list
  only links that still work, now with `ExpiresAt`.
- **Issuing again is the fix.** Asking for the link (`POST .../link`) when the one out has
  expired issues a new one, `replace` or not -- "send link" means a link that works -- and
  ends the old one the usual way, forgetting who opened it.
- **And restarts the rejoin rule.** The new link's `LinkIssuedAt` is now, so somebody who left
  after the expired link went out, and before this one, may come back by it -- the same as
  for any link issued after they left.

### The list of links you have opened

Somebody opens a link, signs in, and goes to look at something else. `Describe` records the
pair in `InvitationOpened` and `Mine` reads it back -- the "Waiting on you" row on the home
page and `invitations list` -- joined to the guests whose link still works, so a link that
has ended is never offered. It carries the tokens and discloses nothing, since holding the
token is what put the row there.

### "This isn't me"

Declining is done by whoever holds the link, since the link is the only credential there is,
and it only stops the link. The guest stays, holding everything recorded for them, and a
member can send a new one. Nothing moves, so `InvitationClosedResponse` reports zeros -- it
keeps its old shape for clients built when declining handed the guest's position to a member.

## Claiming

`IInvitationService.Claim` is the one moment a position changes hands, in one transaction:

1. the guest ends as `Claimed` and the link with it -- the concurrency token is what decides a
   race;
2. `HandOver` moves the stand-in's shares, the transactions it was down as having paid for,
   and its places in the group's split rules onto the claimer;
3. the claimer joins the group, through `IGroupJoiner.Join`.

No amount changes. Every transaction still divides into exactly its own amount, so the
group's balances read the same either side of it. The answer says what moved, and counts the rule places separately (`RulesTaken`), because a
rule place goes on giving them a share of every expense filed under it.

Where the claimer already held a share of the same transaction, the two are added into one
row: a transaction may hold only one opinion about what a person owed.

**A member claiming a guest who turns out to be them** (the group named the same person
twice) merges the guest into them, and the claim page asks "are you and Pat the same
person?" first. Any repayment recorded between the two would now read as the member paying
themselves, so `HandOver` deletes those rather than repointing them and the answer counts them
(`SettlementsCancelled`, `AmountCancelled`). They netted to nothing for whoever holds both
ends, so the member's balance is what the two came to together.

**Following the group's open join link is not claiming a guest, and does not end one.**
Nothing has told the group that the person who walked in is the guest it named, and guessing
from a name would be the mistake this design refuses. A member can remove the guest once
square, or send them their link.

## Deprecated routes

The API still serves the invitation routes the published CLI and older skills call, marked
deprecated in the OpenAPI document:

- `POST /groups/{id}/invitations` adds guests and issues each a link;
- `GET /groups/{id}/invitations` lists the guests with a link out, by their row id;
- `DELETE /groups/{id}/invitations/{invitationId}` stops that guest's link and moves nothing.

Every `IsGuest` flag is also written as `isPendingInvitee` (and `paidByIsGuest` as
`paidByIsPendingInvitee`). `SETTLEMENT_WITH_PENDING_INVITEE` and `GROUP_MEMBER_NOT_JOINED`
are never sent.

## Where it is decided

| Question | Where |
| --- | --- |
| Who may be given a share, or settled with | `IGroupParticipants.Of` / `IdsOf` / `Find` |
| What somebody is to a group: member, guest, departed, nobody | `IGroupParticipants.StandingOf` |
| Adding a guest, issuing and stopping their link | `IGuestService` |
| Whose balances appear | `GroupService.NetBalances`, `GetAllGroupNetBalances` |
| Who is in the settlement plan | `DebtCalculationService.MinimizeTransactions` -- everybody |
| Who a repayment may name | `SettlementService.Settle`, `SettlementService.RecordRepayment`, `TransactionService.Update` |
| Removing a guest | `GroupService.RemoveGroupMember` → `DetachGuest` |
| What happens when a link is claimed | `InvitationService.Claim`, `IGroupParticipants.HandOver` |
| What happens when one is declined | `InvitationService.Decline`: the link stops |
| Which of two writes to a guest wins | the `Token` and `EndedAt` concurrency tokens |
| What an account has open | `InvitationService.Mine`, written by `Describe` |
| Whether a departed person may use a link | `IGroupJoiner.Join`, `EnsureMayRejoin`, against `LinkIssuedAt` |
| Whether a link has expired | `GroupGuest.HasWorkingLink`, `LinkLifetime` from `LinkIssuedAt` |
| What becomes of the guest's own "all for them" rule | `IMemberSplitRules.HandOver` on a claim; kept, like a leaver's, on removal |

## Known gaps

- **Nobody is told about a link.** The link is the whole of the notification, and sending it
  is the group's job. Closing this properly means a channel to reach somebody on.
- **A forwarded link can be claimed by the wrong person.** Single use limits the damage to one
  taking, and the claim page says whose place it is before anybody presses anything, but
  nothing verifies that the claimer is who the group meant.
- **A member cannot merge a guest into an existing member without a link.** "Pat is actually
  Sam" has to go through Pat's link, claimed by Sam.
- **A removed guest cannot be restored.** Adding them again by name makes a new guest with a
  new stand-in.
- **An expired link is not told apart.** Whoever holds one hears what a claimed or mistyped
  one gets, so a guest who simply waited too long cannot tell that a new link would work.
  That is the price of not confirming to a stranger that the link was once good; the claim
  page says expiry is one of the reasons.
- **Nobody is told a link ran out.** The group sees it on the guest's row when it looks.
- **Stand-ins accumulate**, one per guest ever added. They are tiny and hold the history, so
  this is deliberate, but nothing ever cleans them up.
