namespace GroupSplit.Data.Entities;

/// <summary>
/// Equally between people.
/// </summary>
/// <remarks>
/// Naming nobody is the useful case and the default: an even rule with no participants
/// divides between whoever is in the group at the time, so it keeps dividing evenly when
/// somebody joins instead of freezing today's membership into weights. Naming people
/// narrows it -- "evenly, but only between the three of us who were on the trip".
/// </remarks>
public sealed class EvenSplitRule : WeightedSplitRule;
