namespace GroupSplit.Shared;

/// <summary>
/// One page of a listing, and what it is a page of. <see cref="Page"/> and
/// <see cref="PageSize"/> are what the server applied, which is not always what was asked
/// for: see <see cref="PageRequest.Normalized"/>.
/// </summary>
/// <remarks>
/// Deliberately no computed members -- no <c>TotalPages</c>, no <c>HasNext</c>. They would
/// be serialized, land in the OpenAPI schema, and have to be restated in every
/// <c>PagedResponseOf…</c> below; a caller can divide.
/// </remarks>
public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// The wire type for a page of transactions, and the reason it has this name.
/// </summary>
/// <remarks>
/// The clients are generated from the API's OpenAPI document with
/// <c>/GenerateDtoTypes:false</c>, so a schema id is written into the generated client
/// verbatim, as a C# type name. A closed generic is named <c>{Type}Of{Argument}</c> -- the
/// same convention the <c>JsonPatchDocumentOf…</c> schemas already follow -- and the client
/// templates only rewrite that shape back into a generic for <em>parameters</em>, never for
/// a response type. So the name below has to exist as a type, or the app does not compile.
/// <para>
/// It derives from <see cref="PagedResponse{T}"/>, so nothing else has to know: endpoints
/// declare and return the generic, application code reads the generic, and this exists only
/// where the generator looks. <see cref="GroupSplit.API.Extensions"/> pins the schema id to
/// this name so the two cannot drift apart.
/// </para>
/// </remarks>
public sealed record PagedResponseOfTransactionResponse(
    IReadOnlyList<TransactionResponse> Items,
    int Page,
    int PageSize,
    int TotalCount)
    : PagedResponse<TransactionResponse>(Items, Page, PageSize, TotalCount);

/// <summary>
/// The wire type for a page of the caller's shares. Exists for the same reason
/// <see cref="PagedResponseOfTransactionResponse"/> does, and nothing else reads it.
/// </summary>
public sealed record PagedResponseOfExpenseShareResponse(
    IReadOnlyList<ExpenseShareResponse> Items,
    int Page,
    int PageSize,
    int TotalCount)
    : PagedResponse<ExpenseShareResponse>(Items, Page, PageSize, TotalCount);
