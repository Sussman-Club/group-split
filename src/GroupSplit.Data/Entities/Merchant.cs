namespace GroupSplit.Data.Entities;

/// <summary>
/// Somewhere money gets spent -- "Blue Bottle Coffee", "Lidl" -- and the logo that says so
/// at a glance.
/// </summary>
/// <remarks>
/// One row per place rather than a logo column on every payment. A card used at the same
/// shop forty times is forty imported rows and, once they are filed, forty expenses; the
/// logo is a fact about the shop and not about any one of those payments, so it is stored
/// once and pointed at. That is also what lets an expense show it: the ledger never carried
/// the provider's enrichment columns and should not start, but a foreign key to a place is
/// an ordinary thing for a transaction to have.
/// <para>
/// Global and not per-group, because a merchant is not a group's opinion -- unlike
/// <see cref="Category"/>, which is exactly that. Two groups that both shop at Lidl share
/// this row.
/// </para>
/// </remarks>
public class Merchant : Entity
{
    /// <summary>
    /// The name to show, cased as the provider wrote it the first time we saw it.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// <see cref="Name"/> folded for matching, and the key that keeps this table one row
    /// per place: lower-cased and trimmed.
    /// </summary>
    /// <remarks>
    /// Stored rather than computed in the query, so the unique index can be on it and the
    /// resolver's lookup can use that index. A provider that starts sending "LIDL" where it
    /// used to send "Lidl" finds the row it already has instead of making a second one.
    /// </remarks>
    public required string NormalizedName { get; set; }

    /// <summary>
    /// The merchant's logo, as the provider hosts it. Null for a place no provider had a
    /// logo for, which renders as initials -- the same fallback a person without a picture
    /// gets.
    /// </summary>
    public string? LogoUrl { get; set; }

    /// <summary>
    /// When this place was first seen. Kept because the row is created as a side effect of
    /// a sync, and a table that grows on its own is worth being able to date.
    /// </summary>
    public required DateTimeOffset FirstSeenAt { get; set; }
}
