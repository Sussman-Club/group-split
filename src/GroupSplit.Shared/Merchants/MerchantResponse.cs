using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

/// <summary>
/// Somewhere money gets spent, and the logo that says so at a glance.
/// </summary>
/// <param name="LogoUrl">
/// The mark to show, or null for a place that renders as initials. Absolute where a bank
/// provider hosts it and root-relative where the app serves it itself.
/// </param>
/// <param name="TransactionCount">
/// How many transactions point at this. The number that says whether renaming it is a
/// tidy-up or a rewrite of somebody's history, and whether deleting it will be refused.
/// </param>
/// <remarks>
/// Shared, and not a group's own the way a <see cref="CategoryResponse"/> is: two groups
/// that both shop at Lidl see this same row, which is the point of it -- a logo stored once
/// for the place reaches every expense at that place at once.
/// </remarks>
public sealed record MerchantResponse(
    Guid Id,
    string Name,
    string? LogoUrl,
    DateTimeOffset FirstSeenAt,
    int TransactionCount);

/// <summary>
/// A place to add by hand, for spending no bank imported.
/// </summary>
/// <remarks>
/// The sync creates these on its own from what a provider says. This is the other way in:
/// somebody who pays the same shop in cash every week, or who wants a mark on a group that
/// has no bank linked at all, and who has nothing to wait for a provider to enrich.
/// </remarks>
public record CreateMerchantRequest
{
    [Required(ErrorMessage = "Name is required")]
    [StringLength(128, ErrorMessage = "Name must be less than 128 characters.")]
    public string Name { get; init; } = null!;

    [StringLength(512, ErrorMessage = "Logo URL must be less than 512 characters.")]
    public string? LogoUrl { get; init; }
}

/// <summary>
/// A correction to a place: its name, or the mark it shows.
/// </summary>
/// <remarks>
/// Both fields are always sent, so leaving <see cref="LogoUrl"/> out clears it. That is
/// deliberate and is the difference from the sync, which only ever fills a missing logo in
/// -- a provider that stops sending one is having a bad afternoon, whereas a person who
/// clears the field means it.
/// </remarks>
public record UpdateMerchantRequest
{
    [Required(ErrorMessage = "Name is required")]
    [StringLength(128, ErrorMessage = "Name must be less than 128 characters.")]
    public string Name { get; set; } = null!;

    [StringLength(512, ErrorMessage = "Logo URL must be less than 512 characters.")]
    public string? LogoUrl { get; set; }
}
