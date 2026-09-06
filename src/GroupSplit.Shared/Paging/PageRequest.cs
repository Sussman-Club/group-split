namespace GroupSplit.Shared;

/// <summary>
/// Offset paging, as every list endpoint that pages takes it: <c>?Page=2&amp;PageSize=50</c>.
/// </summary>
/// <remarks>
/// Out-of-range values are clamped rather than refused, and the page that comes back says
/// which values were actually applied. Refusing would be the stricter contract, but the
/// annotations that would express it (<c>[Range]</c>) only run inside the API's own
/// assembly -- .NET 10 validation is a source-generated interceptor on the
/// <c>AddValidation()</c> call site -- so a caller hosted anywhere else would silently get
/// the lenient behaviour instead. One behaviour everywhere is worth more here than the
/// stricter of the two.
/// </remarks>
public record PageRequest(int Page = 1, int PageSize = PageRequest.DefaultPageSize)
{
    public const int DefaultPageSize = 25;

    /// <summary>The most rows one request may ask for, whatever it asks for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>This request with both values brought inside their bounds.</summary>
    public PageRequest Normalized() => new(Math.Max(Page, 1), Math.Clamp(PageSize, 1, MaxPageSize));
}
