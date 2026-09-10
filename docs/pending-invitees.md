# Somebody who has been invited and has not answered

Inviting somebody is the moment a group starts, and it is exactly the moment there is a
backlog of expenses to enter: the trip that was just booked, the flat that was just moved
into, the dinner that prompted the invitation. Until [#236][236] a group had to either wait
for every invitee to sign up before recording anything, or record it now with the shares
wrong and fix them one by one later. Both leave the ledger untrue for as long as somebody
sits on their invitation, and the second quietly builds up work nobody remembers to do.

So a pending invitation is now a participant the group can point at. This document is about
what that means and, mostly, about the arithmetic it must not break.

[236]: https://github.com/Sussman-Club/group-split/issues/236

## An invitation is a name and a link, not an address

This is the part that changed most, and it is worth saying why before anything else.

An invitation used to be an email address. That address was three things at once: the handle
the row was keyed on, the label every screen showed, and the way an invitee found their own
invitations. And it limited a group to asking people whose email somebody happened to have,
which is not how most people know the friend they went on the trip with.

Now the group **names** the person and gets a **link** to send them, through whatever they
actually talk on. Whoever opens that link and claims it becomes that person.

What was given up, honestly: **nobody is told they have been invited.** With no address
there is nothing to notify, so an existing GroupSplit user gets no badge and no email — the
link is the whole of the introduction, and sending it is the group's job.

There *is* still an invitee-side list, and it answers a question a link can support:
**every invitation whose link I have opened.** `GET /invitations` and `invitations list`
came back with that meaning, which is why `Describe` writes down who opened what -- see
[the list](#the-list-of-links-you-have-opened) below.

## Membership and participation are different questions

**Membership** is who may read and change a group. Every authorization check in the app asks
that of `Group.Users` and must go on asking it of nothing else.

**Participation** is who may be given a share. It is the wider set -- members plus the people
the group has named and is waiting on -- and it is what `IGroupParticipants` answers.

A pending invitee is choosable wherever a person is chosen *for money*: a transaction's
payer, a stated split, a split rule's participants. They are choosable nowhere else. They
cannot see the group, cannot act in it, and there is nobody to settle up with. What they have
is a position in its balances, waiting for whoever claims it.

The seam matters because the two sets are one call apart in the code and worlds apart in what
they authorize. `GroupParticipants.Of` is a read for money. It is never an answer to "may
this person do that".

## A participant is a user row

`GroupInvitation` carries a required `ParticipantUserId`: the `User` every share recorded
against that person belongs to. It is always a **stand-in** -- a row with the given name on
it, no address, and no `UserIdentity`, so nobody can sign in as it -- and never an existing
account. There is no address to recognise an account by, and guessing from a name would hand
a stranger's ledger to whoever the group happened to call Dani.

The alternative -- a nullable user beside a nullable invitation on `TransactionSplit` -- was
rejected. It puts two columns on the hottest table in the model and makes every balance query
in the app add two sums together, forever, to express something a single foreign key already
says.

A stand-in is not a placeholder person. It exists only while an invitation is pending:
claiming moves its position onto the claimer's own account and deletes it, and declining or
withdrawing hands the position to a member and deletes it. Nothing ever leaves one lying
around.

## The link

`GroupInvitation.Token` is 24 bytes from the cryptographic generator, Base64Url, like
`GroupJoinLink.Token` -- and it guards much more.

A **join link** lets somebody into a group as themselves, claiming nothing. It is reusable
and it expires.

A **personal invitation link** hands over a position in the group's ledger. Forward it and
somebody else inherits the debts recorded against that name. So:

- **Single use.** Claiming deletes the invitation, and the token lives on that row, so a
  forwarded copy has nothing left to claim and gets the same 404 a mistyped link does.
- **It says nothing about the money.** `GET /invitations/claims/{token}` answers the group's
  name, its size, who asked and which person the invitation was made out to.
  What is recorded against that person is the group's until the claim makes it somebody's.
- **It lives where the ledger lives.** The tokens come back on
  `GET /groups/{id}/invitations`, which is scoped to the group's own members. That scoping is
  load-bearing, not tidiness.
- **The group can end it.** Withdrawing deletes the row, so the link stops working.

The web app copies the link rather than printing it on the page, for the same reason: a URL on
screen is a URL in a screenshot.

## The list of links you have opened

Somebody opens their link, signs in, and goes to look at something else without claiming or
declining. Nothing breaks -- the invitation stays pending, the link still works, the group
still says it is waiting -- but until this, the app had no way to put it back in front of
them. It had just met them, and threw that away; the only route back was the chat thread the
link arrived in.

So `Describe` records the pair in `InvitationOpened`: this account has seen this invitation.
`Mine` reads it back, and the row is in "Waiting on you" on the home page and in
`invitations list`.

Three things about it are worth stating, because it looks like a lesser version of the
address matching it replaces:

- **It is not lesser.** Address matching found an invitation only when the group happened to
  have the same address the person later signed up with. This finds it for whoever actually
  opened the link, whatever their address is.
- **It carries the tokens.** That is the point -- the list exists to get somebody back to a
  link they no longer have the message for -- and it discloses nothing, since holding the
  token is what put the row there.
- **It cannot drift.** Answering an invitation deletes it and the rows cascade, so "still
  open" is not a flag anybody has to remember to clear.

It does not claim anything, and the row deliberately has no Accept button: claiming takes on
whatever the group has recorded against that name, and the page that says so is `/claim`.
One tap in a list is not somewhere to agree to a position nobody has been shown.

## The two invariants

Everything else here is a consequence of these.

1. **Every transaction's shares sum to its amount.** Stored once, when the transaction is
   written or edited; see `TransactionSplit`.
2. **A group's balances sum to zero**, which follows from (1) as long as every share belongs
   to somebody in the listing.

That second clause is why the group's balances *include* pending invitees. A listing of
members alone would show a column that does not add up, with the missing side belonging to
nobody on the page. `GroupNetBalance.IsPendingInvitee` marks the rows so a client can say
which is which.

It is also why they are **out of the settlement plan**. The plan is a list of payments
somebody can make, and there is no account on the other end of a payment to a pending
invitee: `SETTLEMENT_WITH_PENDING_INVITEE` refuses one by name rather than reporting them
missing. So the plan does not clear everybody, which is the truth -- a group cannot square up
with a person who has not joined.

## Claiming

`IInvitationService.Claim` is the one moment a position changes hands:

1. the claimer joins the group;
2. `HandOver` moves the stand-in's shares and the transactions it was down as having paid for
   onto the claimer's account;
3. the invitation and the stand-in both go.

No amount changes. Every transaction still divides into exactly its own amount, so the
group's balances read the same on either side of it -- the same numbers under a different
name. The answer says what moved, because somebody's balance did.

Where the claimer already held a share of the same transaction, the two are added into one
row: a transaction may hold only one opinion about what a person owed, and the unique index
says so.

**Following the group's open join link is not claiming an invitation, and does not answer
one.** Nothing has told the group that the person who walked in is the person it named, and
guessing from a name would be the mistake this design refuses. The group goes on waiting;
withdrawing the invitation is how it says otherwise, and that hands the position over rather
than dropping it.

## Declining and withdrawing: the case that needed deciding

Money already recorded against a person cannot silently disappear, and it cannot be left
where nothing can ever claim it. So:

> **Declining or withdrawing an invitation hands everything it was holding, in full, to one
> member of the group.**

Concretely, in `IGroupParticipants.HandOver`:

- every share of theirs in that group moves to the absorber -- added into the absorber's own
  share where they already had one on the same transaction;
- every transaction they were down as having paid for becomes the absorber's;
- their place in the group's split rules goes. A rule is a template for the next expense
  rather than a record of anything, and weights are proportional, so dropping the name
  divides what was theirs among the rest -- exactly what happens when a member leaves
  (`GroupService.DetachMember`).

No amount changes. Every transaction still sums to its own amount, so the group's balances
still sum to zero.

**Who absorbs**: the member who sent the invitation, or -- if they have since left -- the
group's longest-standing member. A group with no members left has neither, and then nothing
moves and the stand-in stays: its shares are worth more where they are than cascaded away by
tidying up the row that holds them. `TransactionSplit.UserId` and `Transaction.UserId`
cascade, so deleting a stand-in that still holds shares would take them silently, and a
group nobody is in is not a column anybody is reading. Not a choice the caller makes, and deliberately the same
rule on both sides: the group is present when it withdraws one and could be asked, but
whoever declines is not in the group and could not be asked at all, and an outcome that
depended on who pressed the button would be two rules for one event.

**Declining is done by whoever holds the link**, since the link is the only credential there
is. A forwarded link can therefore be used to turn an invitation down, which is recoverable
-- the group invites again -- and much less costly than the claim it could otherwise make.

**Everyone is told.** `POST /invitations/claims/{token}/decline` and
`DELETE /groups/{id}/invitations/{invitationId}` used to answer 204 and now answer an
`InvitationClosedResponse` saying what moved and whose it is now. The web app says it in a
snackbar; the CLI prints it, and gates both commands behind the confirmation protocol,
because a command that can move somebody's balance is not a command that should run
unannounced. And the move is visible to the whole group in the ledger, on the rows that
changed hands.

### Why not refuse instead

`GROUP_MEMBER_NOT_SETTLED` refuses to remove a member with a non-zero balance, and the
symmetry is tempting. It does not work here. You cannot settle up with a pending invitee --
that is out of scope, and there is nobody to pay -- so the only way to zero one would be to
edit every expense that named them. A group could hold an invitee hostage, and an invitee
could never decline.

### Deleting an account

Nothing to do, and worth knowing why. `AccountService.DeleteAccount` gates on the groups the
account is a **member** of, and an unanswered invitation is not one -- so a position held in
their name in a group they never joined would slip past that gate. It cannot arise: an
invitation holds a stand-in of its own, and claiming is what attaches an account to it, which
deletes both. No pending invitation can name a real account.

This is exactly what an address-based invitation did wrong, and the reason it is called out
here rather than left implicit.

## Where it is decided

| Question | Where |
| --- | --- |
| Who may be given a share | `IGroupParticipants.Of` / `IdsOf` |
| Whether an expense's payer is allowed | `TransactionService.ParticipantOf` |
| Whether a stated split is allowed | `ExpenseSplitter.MembersOf` |
| Whether a rule may name them | `SplitRuleService.Validate` |
| Whose balances appear | `GroupService.NetBalances`, `GetAllGroupNetBalances` |
| Who is in the settlement plan | `DebtCalculationService.MinimizeTransactions` |
| Who a repayment may name | `GroupService.Settle`, `SettlementService.RecordRepayment`, `TransactionService.Update` |
| What a named person's participant is | `IGroupParticipants.StandInFor` |
| What an account has open | `InvitationService.Mine`, written by `Describe` |
| What happens when a link is claimed | `InvitationService.Claim`, `IGroupParticipants.HandOver` |
| What happens when one is declined or withdrawn | `InvitationService.Close`, `IGroupParticipants.HandOver` |

## Known gaps

- **Nobody is told they were invited.** The link is the whole of the notification, and
  sending it is the group's job. An existing GroupSplit user gets no badge and no mail until
  they open the link once -- after that it is in their own list and stays there. Closing this
  properly means a channel to reach somebody on, which is a bigger question than invitations.
- **A forwarded link can be claimed by the wrong person.** Single use limits the damage to
  one taking rather than many, and the claim page says whose name it is before anybody
  presses anything, but nothing verifies that the claimer is who the group meant. Binding a
  claim to something the group can check would be the next step.
- **An account deleting itself can empty a group.** `AccountService.DeleteAccount` detaches
  from every group without the last-member check `GroupService.Leave` applies, so a group
  can end up with no members and a pending invitation nobody can absorb. Nothing is lost
  when that happens -- see above -- but the group is then unreachable and holds a position
  that can never move. The guard belongs on the deletion, not here.
- **A withdrawn invitation leaves no trace of the name.** The ledger searches
  `User.FirstName`, and a stand-in's name lives there, so an open invitation is findable --
  but withdrawing deletes the stand-in, and the rows it left behind read as the absorber's
  with nothing to say they were somebody else's an hour ago.
