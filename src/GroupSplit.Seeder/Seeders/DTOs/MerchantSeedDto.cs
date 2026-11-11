namespace GroupSplit.Seeder.Seeders.DTOs;

/// <summary>
/// A seeded place money gets spent, and the mark that stands for it.
/// </summary>
/// <remarks>
/// The demo data's shops are seeded as rows of their own rather than made on the way past
/// by whichever seeder mentions them first: the bank rows and the expenses both point at
/// these, they run at the same time as each other, and a shared table two writers can
/// create in is a race the unique index would turn into a failed seeding run.
/// </remarks>
public class MerchantSeedDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// The name of the mark to show, without a path or an extension -- <c>lidl</c> for
    /// <c>merchants/lidl.svg</c>. Null for a shop that renders as initials, which is worth
    /// seeding too: it is what a real merchant the provider has no logo for looks like.
    /// </summary>
    /// <remarks>
    /// A name and not a URL, because the URL is a fact about where the app serves its
    /// static files from and not about the shop -- and because a seed file full of
    /// <c>_content/GroupSplit.App.Shared/...</c> would be a seed file nobody could read.
    /// The seeder makes the URL; see <c>MerchantSeeder</c> for what these files are.
    /// </remarks>
    public string? Logo { get; init; }
}
