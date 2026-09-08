namespace GroupSplit.Shared;

/// <summary>
/// Offset paging, as every list endpoint that pages takes it: <c>?Page=2&amp;PageSize=50</c>.
/// </summary>
/// <remarks>
/// Out-of-range values are clamped rather than refused, and the page that comes back says
/// which values were actually applied. Refusing would be the stricter contract; clamping is
/// chosen because a page size is a request for how much, not an assertion about the world,
/// and answering "here is as much as you may have, and here is how much that was" is more
/// use to a caller than a 400. There are deliberately no <c>[Range]</c> annotations here.
/// </remarks>
public record PageRequest(int Page = 1, int PageSize = PageRequest.DefaultPageSize)
{
    public const int DefaultPageSize = 25;

    /// <summary>The most rows one request may ask for, whatever it asks for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>This request with both values brought inside their bounds.</summary>
    public PageRequest Normalized() => new(Math.Max(Page, 1), Math.Clamp(PageSize, 1, MaxPageSize));
}
