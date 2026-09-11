using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Commands;

/// <summary>
/// The places money gets spent, as the picker on an expense needs them. See
/// <see cref="IGroupCommands"/> for why these exist at all.
/// </summary>
/// <remarks>
/// A merchant belongs to nobody -- "Lidl" is not a fact about a group, and two groups that
/// both shop there share the row -- so unlike a category there is no group to scope these
/// to and nothing here takes one.
/// <para>
/// The two reads say nothing when they fail. They feed a type-ahead, and a snackbar per
/// keystroke on a flaky connection is a stream of them; the field simply offers nothing,
/// which is what it would offer anyway. <see cref="CreateAsync"/> is a write somebody
/// asked for by name and does announce.
/// </para>
/// </remarks>
public interface IMerchantCommands
{
    /// <summary>Places whose name contains <paramref name="search"/>, for a type-ahead.</summary>
    /// <returns>Empty when the read failed; the field offers nothing either way.</returns>
    Task<IReadOnlyList<MerchantResponse>> SearchAsync(string? search, CancellationToken ct = default);

    /// <summary>
    /// One place by id, to show the name against an expense that already names it.
    /// </summary>
    Task<MerchantResponse?> GetAsync(Guid merchantId, CancellationToken ct = default);

    /// <summary>
    /// Adds a place by hand, for spending no bank imported.
    /// </summary>
    /// <remarks>
    /// The sync writes this table on its own from what a provider says; this is the other
    /// way in, and the reason the picker offers it at all. A group with no bank linked has
    /// an empty list, so without this the field would be one nobody could ever fill.
    /// </remarks>
    Task<MerchantResponse?> CreateAsync(string name, CancellationToken ct = default);
}
