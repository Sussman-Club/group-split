namespace GroupSplit.Seeder.Seeders.DTOs;

/// <summary>
/// Somebody asked to join a seeded group and not yet answered.
/// </summary>
public class InvitationSeedDto
{
    public required Guid Id { get; init; }

    public required Guid GroupId { get; init; }

    /// <summary>
    /// The address that was asked. Most of these belong to nobody, which is the case
    /// invitations exist for -- inviting a friend who has not signed up yet.
    /// </summary>
    public required string Email { get; init; }

    /// <summary>
    /// Who asked, or null for one that outlived the account that sent it. The column is
    /// nullable on purpose, so the demo data covers it: the members tab has to render a
    /// pending row with nobody to credit it to.
    /// </summary>
    public Guid? InvitedByUserId { get; init; }

    /// <summary>
    /// How long ago, rather than when.
    /// </summary>
    /// <remarks>
    /// Every other seed file carries absolute dates, because an expense happened on a day
    /// and that day does not move. An invitation is a <em>pending</em> thing, and a pending
    /// row stamped with a fixed date reads as broken the moment the seed data is a few
    /// months old -- "invited 14 March 2026" on a screen that also says nobody has answered
    /// looks like a bug in the app rather than the age of a fixture. Resolved against the
    /// clock at seed time, so "invited 2 days ago" is true whenever somebody seeds.
    /// </remarks>
    public required int DaysAgo { get; init; }
}
