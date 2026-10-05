namespace GroupSplit.Seeder.Seeders.DTOs;

/// <summary>
/// A guest of a seeded group: somebody it shares costs with who has no account in it.
/// </summary>
public class GuestSeedDto
{
    public required Guid Id { get; init; }

    public required Guid GroupId { get; init; }

    /// <summary>
    /// What the group calls them. The whole of what it knows: nobody has signed in as this
    /// person, which is the case guests exist for.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The token in their link, or null for a guest nobody has sent one -- the ordinary
    /// case. Stated rather than generated so it is the same on every run, which is what lets
    /// a developer open <c>/claim/{token}</c> and see the claim page without hunting through
    /// the database first.
    /// </summary>
    /// <remarks>
    /// Demo tokens, and readable on purpose: a real one is 24 bytes from the cryptographic
    /// generator, and these are fixtures in a database anybody can reset.
    /// </remarks>
    public string? Token { get; init; }

    /// <summary>
    /// Who added them, or null for one that outlived the account that added them. The
    /// column is nullable on purpose, so the demo data covers it: the people tab has to
    /// render a guest with nobody to credit them to.
    /// </summary>
    public Guid? AddedByUserId { get; init; }

    /// <summary>
    /// The id to give the stand-in account made for this person.
    /// </summary>
    /// <remarks>
    /// Stated rather than generated so it is the same id on every run, which is what lets
    /// the rest of the seed data name it -- an expense they paid for, a rule that gives them
    /// a share.
    /// </remarks>
    public Guid? ParticipantUserId { get; init; }

    /// <summary>
    /// How long ago, rather than when.
    /// </summary>
    /// <remarks>
    /// Every other seed file carries absolute dates, because an expense happened on a day
    /// and that day does not move. An invitation is a <em>pending</em> thing, and a pending
    /// row stamped with a fixed date reads as broken the moment the seed data is a few
    /// months old -- "invited 14 March 2026" on a screen that also says nobody has answered
    /// looks like a bug in the app rather than the age of a fixture. Resolved against the
    /// clock at seed time, so "added 2 days ago" is true whenever somebody seeds.
    /// </remarks>
    public required int DaysAgo { get; init; }
}
