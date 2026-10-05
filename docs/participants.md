# Participants: who is in a group, and what each may do

A group's people are not all the same kind of person. Some are in it, some are guests it
keeps track of without their having an account, some have left. This page says what each of them is, how the API tells them
apart, and what each may and may not do -- with the code every refusal answers with.

For the two neighbours of this page: [guests.md](guests.md) is the whole story of guests and
their links, and [split-rules-and-membership.md](split-rules-and-membership.md) is what
membership does to split rules.

## Four standings

What somebody is to one group is one question with four answers, and
`IGroupParticipants.StandingOf` is the one place that asks it.

| Standing | What it is | How it is told |
| --- | --- | --- |
| **Member** | In the group. May read it and change it. | An open membership row: `GroupMembership` with `LeftAt` null. |
| **Guest** | Somebody the group shares costs with who has no account in it. A stand-in account nobody can sign in as; the members record everything for them. | A current `GroupGuest` (`EndedAt` null) whose `ParticipantUserId` it is. |
| **Departed** | Was a member or a guest and is not now: left, was removed, deleted their account, or was removed as a guest. | A closed membership row (`LeftAt`, `LeftReason`, `LeftByUserId`), or a name the group's ledger or rules still carry -- which is what is left of a removed guest, and of departures from before membership rows were kept. |
| **Stranger** | None of the above. | |

Two sets are built from these, and the difference between them is the point of the model:

- **Membership** is access. Every "may this person read or change the group" check asks it of
  `Group.Users`, which a query filter on `GroupMembership` restricts to open rows.
- **Participation** is the ledger: members and guests, `IGroupParticipants.Of`. A
  participant can pay, hold a share, be named by a rule and be settled with, whether they have
  an account in the group or not.

## Members are equal

There are no roles. No owner, no admin, and the creator of a group or whoever added a guest
has no power the others lack. Any member may add guests, issue, replace or stop any guest's
link, make or revoke the join link, record any repayment, and remove any other member or
guest. The one asymmetry is
that you cannot remove yourself: that is leaving.

## Removed and left

Both end in the same standing -- departed -- and differ only on the way out:

- who acted, which `LeftReason` and `LeftByUserId` record;
- leaving is refused to the last member (`GROUP_CANNOT_LEAVE_LAST_MEMBER`), removing has no
  such case because you cannot remove yourself;
- **removing puts out the group's live join link**, because the person removed could read it
  and may pass it on. Leaving does not: the link is the group's, and leaving expresses no
  distrust.

Both require a zero balance first (`GROUP_MEMBER_NOT_SETTLED`).

A guest is removed by the same route on the same terms -- square first, and kept in the
history -- but nothing about the join link changes: a guest never had a way to read it.

## Who may do what

✓ allowed · ✗ refused, with the code · — does not arise · † changed by the work on #310 and #314 · ‡ changed by guests

| Action | Member | Guest | Departed | Stranger |
| --- | --- | --- | --- | --- |
| Read the group, its members, balances, activity | ✓ | — nobody signs in as a stand-in; whoever holds the link is a stranger until they claim it | ✗ 404 `GROUP_NOT_FOUND` | ✗ 404 `GROUP_NOT_FOUND` |
| Read a particular expense | ✓ any in the group | — | ✓ only ones they paid for; otherwise ✗ 404 `TRANSACTION_NOT_FOUND` | ✗ 404 `TRANSACTION_NOT_FOUND` |
| Change or delete an expense they can read | ✓, subject to the departed rows below | — | ✗ 409 `TRANSACTION_GROUP_LEFT` | ✗ 404 |
| Add an expense to the group | ✓ | — | ✗ 404 `GROUP_NOT_FOUND` | ✗ 404 `GROUP_NOT_FOUND` |
| Be the payer of a new expense | ✓ | ✓ | ✗ 409 `TRANSACTION_PAYER_NOT_IN_GROUP` | ✗ 409 `TRANSACTION_PAYER_NOT_IN_GROUP` |
| Hold a share of a new or re-divided expense | ✓ | ✓ | ✗ 409 `SPLIT_USER_NOT_IN_GROUP` | ✗ 409 `SPLIT_USER_NOT_IN_GROUP` |
| Keep a share they already hold through an edit | ✓ | ✓ | † ✓ carried unchanged (a rename used to be refused) | — |
| Be named by a split rule the group writes | ✓ | ✓ | ✗ 400 `RULE_USERS_NOT_IN_GROUP`; departing prunes them from every rule by a new version | ✗ 400 `RULE_USERS_NOT_IN_GROUP` |
| Have a built-in "all for them" rule ("One pays") | ✓ from joining | † ✓ from being added; it becomes the claimer's when somebody claims their link | kept, naming them; stops dividing (`SPLIT_RULE_INVALID`) and leaves the picker | — |
| Appear in the group's balances | ✓ | ✓ marked `isGuest` | ✗ listed under past members instead (a removed guest marked `isGuest`) | ✗ |
| Appear in the settlement plan | ✓ | ‡ ✓ like anybody else (used to be left out) | ✗ | ✗ |
| Be settled with, or named in a repayment | ✓ | ‡ ✓ recorded by a member, at either end -- guest↔guest included (used to be 409 `SETTLEMENT_WITH_PENDING_INVITEE`) | ✗ 404 `USER_NOT_FOUND` | ✗ 404 `USER_NOT_FOUND` |
| Settle up or record a repayment | ✓ (not with themselves: `SETTLEMENT_WITH_SELF`) | — | ✗ 404 `GROUP_NOT_FOUND` | ✗ 404 |
| Add guests | ✓ | — | ✗ 404 `GROUP_NOT_FOUND` | ✗ 404 |
| Issue, replace or stop a guest's link | ✓ any guest's | — | ✗ 404 | ✗ 404 |
| Decline a guest's link ("this isn't me") | ✓ whoever holds the link | — | ✓ whoever holds the link, unless it predates their departure (below) | ✓ whoever holds the link; ‡ the link stops and the guest stays (used to hand their position to a member) |
| Make, read or revoke the join link | ✓ | — | ✗ 404 | ✗ 404 |
| Remove another member or a guest | ✓ if they are settled (`GROUP_MEMBER_NOT_SETTLED`, with the balance); not yourself (403 `GROUP_CANNOT_REMOVE_SELF`) | ‡ as the target: ✓ once square, and kept as a past guest (used to be 409 `GROUP_MEMBER_NOT_JOINED`) | † as the target: ✗ 404 `USER_NOT_FOUND` (used to succeed and change nothing) | as the target: ✗ 404 `USER_NOT_FOUND` |
| Leave | ✓ if settled; not the last member | — | ✗ 404 | ✗ 404 |
| Archive the group in their own list | ✓ | — | ✗ 404 | ✗ 404 |
| Follow the join link | ✓ already in, not an error | — | † ✗ 409 `GROUP_REJOIN_NEEDS_NEW_INVITATION` for a link made at or before they left; a newer one brings them back | ✓ joins as themselves |
| Claim a guest's link | † ✓ after confirming in the app: merges the guest into them; ‡ repayments between the two are deleted, since they would be self-payments | — | † same rule as the join link | ✓ takes the guest's place; † of two claiming at once, one gets in and the other gets 404 `GROUP_INVITATION_NOT_FOUND` |
| Have what they paid or owed on an expense change | ✓ | ✓ | † ✗ never: any edit, re-division, move, bill division or delete that would is 409 `TRANSACTION_MOVES_DEPARTED_BALANCE`, naming them | — |

## What a departure fixes in place

Somebody can only leave square, and the group's balances list only the people still in it.
The column therefore adds up for exactly as long as nothing changes what a departed person
paid or owed. So that is the rule, per transaction: their part stays as it was recorded.

What still works on an expense a leaver was on: renaming it, its note, its date, its
category, re-pointing a line of its bill, and changing the amount with the shares set by
hand, leaving theirs as it is. What is refused: deleting it, re-dividing it, changing the
amount any other way, handing what they paid to somebody else, and moving it to another
group. The same holds for the settlement that let them leave.

`IGroupParticipants.DepartedOn` takes the snapshot before an edit is applied, reading the
transaction itself rather than membership rows -- so a removed guest is covered too -- and
`DepartedPositions` compares.

## Coming back

A way back in is only as good as its date. A join link is dated by when it was made and a
guest's link by when it was issued (`LinkIssuedAt`, not when the guest was added); `IGroupJoiner` refuses either to a person whose membership
ended at or after that moment, on the landing page's read as well as on the join. One issued
afterwards is how a group says it wants them back, and they return as an ordinary member on
the same row, with `JoinedAt` reset.

## What the database holds it to

The services decide who may do what; the schema makes sure that whatever they decide, a bug
cannot write something that makes no sense. Migrations `GroupParticipantsAndMembership` and
`GuestsWithOptionalLinks` added:

| Guarantee | How |
| --- | --- |
| Deleting a person never deletes money | `Transaction.UserId` and `TransactionSplit.UserId` restrict. Nothing deletes a stand-in any more -- a claimed one is left holding nothing -- and if anything tried while it held a position, the delete would fail instead of taking expenses and shares with it. |
| Every membership change is remembered | `GroupMembershipEvent`: one row per join, departure (left, removed, account deleted) and return, and per guest added, removed or claimed, with who did it. Written by `AppDbContext` in the same save as the change, so no path can skip it. `GroupUser` stays the current state and reopens on a return; the history is no longer lost when it does. |
| A departure is written whole | Check constraints on `GroupUser`: `LeftAt` and `LeftReason` are set together, the reason is one of the three, and nobody is recorded as the remover of a standing membership. |
| A guest's end and link are written whole | Check constraints on `GroupGuest`: `EndedAt` and `EndReason` together (`Removed` or `Claimed`), a token only with its issue date, and no token on a guest who has ended. A query filter keeps ended guests out of every read. |
| One stand-in, one guest | A unique index on `GroupGuest.ParticipantUserId`, across groups and ended rows. |
| A link is claimed, replaced, stopped or removed once | `GroupGuest.Token` and `EndedAt` are concurrency tokens: of two writes to one guest at the same moment, the second matches no row and is answered as if the link or guest were gone. |
| Each user row says what it is | `User.Kind`: `Account`, `StandIn` or `Deleted`. A stand-in cannot have an address, a deleted account cannot have a name, and the joiner refuses to make anything but an account a member. |
| One join link in force per group | A partial unique index over unrevoked links. Making a link revokes the last one, expired ones included -- closed at the moment they lapsed, so they still read as expired. |
| One "all for one" rule per person per group | `SplitRule.ProvisionedForUserId` and a partial unique index on it, kept in step by `IMemberSplitRules`. A rule retired by a hand-over belongs to nobody. |
| A group can be deleted with what hangs off it | The default-rule keys on `Group` and `Category` are no action rather than restrict: a rule something defaults to still cannot be deleted on its own, but it no longer blocks its own group going. Nothing deletes groups today. |

`GroupSchemaTest` in `GroupSplit.AppHost.Test` writes past the services at each of these and
checks Postgres refuses.

## Known gaps

- **Departures from before membership rows were kept leave no date**, so any link lets those
  people back in. They are still recognised as departed, from the ledger.
- **The six "is the caller in this group" checks are still six.** Each service has its own;
  they agree because they all read `Group.Users` through the filter, and
  `GroupRejoinTest.A_departed_member_is_a_stranger_to_every_service` pins them together until
  they are folded into one (#338).
- **A forwarded guest link can still be claimed by the wrong person.** Single use limits it
  to one taking; nothing checks who the claimer is. See [guests.md](guests.md).
- **A removed guest cannot be brought back.** Adding them again by name makes a new guest.
