using GroupSplit.Data.Entities;

namespace GroupSplit.API.Services;

/// <summary>
/// What to call somebody, in one place.
/// </summary>
/// <remarks>
/// The address when there is no name, which is not an edge case: an invited address is a
/// participant in the group's money from the moment it is invited, and until somebody signs
/// in as it there is no profile to read a name out of. Every listing that showed
/// <c>FirstName + " " + LastName</c> showed a blank for those.
/// <para>
/// A method, so it runs where the rows have already been read. The same fallback has to be
/// written out longhand inside the queries that build a name in SQL -- a balance listing, a
/// group's activity -- because EF cannot call this on its way to the database. Those say so
/// where they do it.
/// </para>
/// </remarks>
internal static class People
{
    public static string Display(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return Display(user.FirstName, user.LastName, user.Email);
    }

    public static string Display(string? firstName, string? lastName, string? email) =>
        $"{firstName} {lastName}".Trim() is { Length: > 0 } name ? name : email ?? string.Empty;
}
