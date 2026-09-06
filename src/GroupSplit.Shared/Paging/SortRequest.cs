namespace GroupSplit.Shared;

/// <summary>
/// How a listing is ordered. <see cref="SortBy"/> is one of the keys the resource offers,
/// matched case-insensitively; absent means the resource's own default order.
/// <see cref="SortDescending"/> absent means the key's natural direction -- dates newest
/// first, names A to Z.
/// </summary>
/// <remarks>
/// Two fields rather than one <c>"dateTime desc"</c> string: a generated client then takes
/// them as an ordinary optional parameter each, and there is no grammar to document or
/// parse. A key the resource does not offer is a 400 naming the ones it does.
/// </remarks>
public record SortRequest(string? SortBy = null, bool? SortDescending = null);
