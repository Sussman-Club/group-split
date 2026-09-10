# What a stored rule means when the membership changes

A split rule is a template: a set of people with weights against their names, kept by the
group so that the next expense in a category does not have to be divided by hand. It is not
a record of anything that happened -- a recorded expense holds the amounts it was actually
divided into, so editing a rule, or the group's membership, cannot restate what anybody owed
last March.

Which leaves one question this document exists to answer: a rule names people, and people
join groups and leave them. What does the rule mean then?

## What is true now

- **A departing member is taken out of the rules that name them.** Not marked, not zeroed:
  the participant row goes. This happens on all three ways out -- removed by somebody else,
  leaving of their own accord, and deleting the account -- because all three go through
  `GroupService.DetachMember`. A pending invitee whose invitation is declined or withdrawn is
  pruned the same way, by `IGroupParticipants.HandOver`.
- **They have to be settled up first.** A member with a non-zero balance in the group cannot
  be removed and cannot leave (`GROUP_MEMBER_NOT_SETTLED`), so a departure never leaves a
  debt behind with nobody to owe it.
- **What was theirs is redistributed among the rest, not left as a hole.** Weights are
  proportional and the division normalises by whatever total it is given, so two members left
  holding one share each divide the whole amount between them.
- **A rule cannot be written naming somebody outside the group.** Create and update both
  refuse it (`RULE_USERS_NOT_IN_GROUP`), so the only way a rule names a non-member is a
  client holding a rule it read before the membership moved. "Outside" means outside the
  group's *participants*, though, which is wider than its members: somebody the group has
  named and is waiting on may be named by a rule too, because the next expense is exactly the
  one they need a share of -- see [pending-invitees.md](pending-invitees.md). If they never
  join, the rule stops naming them, by the same pruning a departure does below.
- **A new member is named by nothing.** Rules are not rewritten when somebody joins, so an
  expense divided by an existing percentage or shares rule gives them no share until the
  rule is edited. An even rule naming nobody -- the default -- includes them from the moment
  they join, which is why naming nobody is the useful thing for an even split to say.
- **The editor edits against today's membership.** `RuleEditorForm` restates the rule it was
  handed before showing it: entries for people who are not members are dropped, and a member
  the rule never named starts at zero. Both directions matter, because the form renders one
  field per member and reads its totals from the whole stored division -- an entry with no
  field is invisible and still counted, and a field with no entry used to throw as the form
  rendered.

## A percentage rule can be left below 100

Taking a member out of a percentage rule leaves the remaining percentages summing to less
than 100: three members on 33.33, 33.33 and 33.34 become two on 33.33, totalling 66.66. This
is deliberate, and it is two different things in two places.

The division does not care. It is proportional, and 33.33 against 33.33 is half each however
the pair is written, so expenses go on being split evenly between the two who are left.

The editor does care, and says so. It shows the true total, marks it as not 100 and refuses
to save until somebody restates the rule -- one click, on **Split Evenly**. Nothing is
renormalised on their behalf: what the departed third becomes is a decision about money
between the people who are still here, and a form that quietly turned 33.33 into 50 would be
making it for them.

The same is not true of shares, which have no total to fall short of. Two members on one
share each are 50/50 whether or not a third once held one, and converting that rule to
percentages says 50 and 50 -- which is the bug in
[#184](https://github.com/Sussman-Club/group-split/issues/184), where the departed member's
share stayed in the divisor and diluted everybody left.

## A rule can still be pruned down to nothing dividable

A shares rule where only the departing member held a share, or a rule naming them alone:
the weights that survive are then all zero, or there are none, and `SplitCalculator.Divide`
refuses both. Somebody invited and never joined leaves a rule the same way, since declining
or withdrawing prunes their name too.

What that costs has changed. It used to be a 500 with a trace id on the next expense filed
under a category pointing at that rule -- a fault report for what is a coherent request
about an incoherent template. `ExpenseSplitter` now turns the refusal into
`SPLIT_RULE_INVALID`, naming the rule and saying the two ways out: edit it, or file the
expense under nothing and have it divided evenly.

Nor is the timing a gap any more, on the invitation paths at least. A hand-over counts the
rules it left naming nobody and reports them, so declining or withdrawing says so at the
moment it causes it -- in the CLI's output and in the app's snackbar -- rather than leaving
it for whoever records the next expense. That count is the one number in
`InvitationClosedResponse` that is a warning rather than a receipt.

Two things it still does not cover. A member **leaving** goes through
`GroupService.DetachMember`, which reports nothing, so a departure can still empty a rule in
silence. And an **even** rule emptied this way does not refuse at all: naming nobody is how
an even split says "between everybody", so it silently widens rather than failing, and the
warning above is the only thing that mentions it.

## Which way a rule moves depends on the direction

Somebody **arriving** keeps their place in a rule. When an invitation is claimed the weight
follows the person onto their own account -- added to a weight they already held, since a
rule may hold only one opinion about somebody -- because the group wrote "Carlos gets one
share" and meant it. One hand-over served both directions at first and pruned in both, so
claiming an invitation quietly took the new member out of the very templates that had named
them. `RuleHandling` is a parameter rather than a default for that reason: passing the wrong
one is invisible.
