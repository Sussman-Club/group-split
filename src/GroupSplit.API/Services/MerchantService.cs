using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface IMerchantService
{
    /// <summary>
    /// The caller's places whose name contains <paramref name="search"/>, alphabetically,
    /// at most <see cref="MerchantService.PageSize"/> of them.
    /// </summary>
    Task<IQueryable<MerchantResponse>> List(string? search, CancellationToken ct = default);

    Task<MerchantResponse> Get(Guid id, CancellationToken ct = default);
    Task<MerchantResponse> Create(CreateMerchantRequest request, CancellationToken ct = default);

    /// <summary>Refused, for every caller. See <see cref="MerchantService"/>.</summary>
    Task Update(Guid id, UpdateMerchantRequest request, CancellationToken ct = default);

    /// <summary>Refused, for every caller. See <see cref="MerchantService"/>.</summary>
    Task Delete(Guid id, CancellationToken ct = default);
}

/// <summary>
/// The places money gets spent, by hand. The sync writes this table on its own from what a
/// provider says; this is the other way in.
/// </summary>
/// <remarks>
/// A merchant belongs to nobody -- "Lidl" is not a fact about a group, and two groups that
/// both shop there share the row -- but that does not make the table everybody's to read.
/// It holds every place any account's bank has ever reported, and a clinic or a lawyer that
/// only one person's card has paid says something about that person. So a caller sees the
/// places they already have something at: an expense they can read, or a row their own bank
/// imported. Those are names their own ledger and inbox show them anyway, so the listing
/// tells them nothing new.
/// <para>
/// Editing is refused outright. Renaming Lidl, changing its logo or deleting it changes what
/// every group that has spent there sees on its own expenses, and nothing in the app says
/// who would be entitled to decide that for them -- there are no roles, only members of
/// groups. Until there is a curator, the endpoints stay and answer
/// <see cref="ErrorCodes.MerchantEditForbidden"/>, so a client is told why rather than
/// meeting a missing route. Adding a place is still open, because a new row changes nothing
/// anybody else sees; adding one that is already there hands back the one there is.
/// </para>
/// </remarks>
public class MerchantService(ICurrentUser userContext, AppDbContext dbContext, TimeProvider clock)
    : IMerchantService
{
    /// <summary>
    /// The most a listing returns. It feeds a type-ahead, which nobody scrolls, so a search
    /// that matches more is a search that wants another letter.
    /// </summary>
    public const int PageSize = 20;

    /// <summary>
    /// The shortest search accepted. One letter matches most of any list and is the cheapest
    /// way to walk it a letter at a time.
    /// </summary>
    public const int MinimumSearchLength = 2;

    public Task<IQueryable<MerchantResponse>> List(string? search, CancellationToken ct = default)
    {
        var query = Mine();

        // Lowered here rather than in the expression, the way every other search in the app
        // does it: it keeps the comparison off the database's collation and translates on
        // both the real provider and the in-memory one the tests use.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim().ToLowerInvariant();

            if (needle.Length < MinimumSearchLength)
                throw new ValidationException(ErrorCodes.MerchantSearchTooShort,
                    $"Search for at least {MinimumSearchLength} characters, or leave the search out.");

            query = query.Where(merchant => merchant.NormalizedName.Contains(needle));
        }

        return Task.FromResult(Describe(query.OrderBy(merchant => merchant.Name).Take(PageSize)));
    }

    public async Task<MerchantResponse> Get(Guid id, CancellationToken ct = default) =>
        await Describe(Mine().Where(merchant => merchant.Id == id)).FirstOrDefaultAsync(ct)
        // The same answer for a place that does not exist and one the caller has nothing
        // at: whether somebody else has spent there is not theirs to learn.
        ?? throw new NotFoundException(ErrorCodes.MerchantNotFound, "Merchant not found.");

    public async Task<MerchantResponse> Create(CreateMerchantRequest request, CancellationToken ct = default)
    {
        var name = request.Name.Trim();
        var normalized = name.ToLowerInvariant();
        var logo = AllowedLogo(request.LogoUrl);

        // One row per place is the whole point of the table. A caller cannot see a place
        // they have nothing at yet, so "already there" is not something they could have
        // avoided, and refusing would leave them unable to name it at all. Handing back the
        // row is what the sync's resolver does with the same name, and the logo asked for is
        // not applied to it: on a row other people share, that would be a relogo.
        var merchant = await dbContext.Set<Merchant>()
            .FirstOrDefaultAsync(existing => existing.NormalizedName == normalized, ct);

        if (merchant is null)
        {
            merchant = new Merchant
            {
                Name = name,
                // The same folding the sync's resolver uses, so a provider that later
                // reports this shop finds the row somebody typed instead of writing a
                // second one.
                NormalizedName = normalized,
                LogoUrl = logo,
                FirstSeenAt = clock.GetUtcNow()
            };

            dbContext.Add(merchant);
            await dbContext.SaveChangesAsync(ct);
        }

        // Not through Mine(): nothing points at it yet, which is the ordinary state of a
        // place somebody has just named and is about to record an expense at.
        return await Describe(dbContext.Set<Merchant>().Where(row => row.Id == merchant.Id)).FirstAsync(ct);
    }

    // Refused before anything is read, so the answer is the same whether the merchant
    // exists or not and the refusal cannot be used to probe for one.
    public Task Update(Guid id, UpdateMerchantRequest request, CancellationToken ct = default) =>
        Task.FromException(Refusal());

    public Task Delete(Guid id, CancellationToken ct = default) =>
        Task.FromException(Refusal());

    private static ForbiddenException Refusal() =>
        new(ErrorCodes.MerchantEditForbidden,
            "A merchant is shared by every group that has spent there, so it cannot be renamed, "
            + "given a different logo or deleted from one account.");

    /// <summary>
    /// What the caller can read and has paid or imported: the expenses (and repayments) in
    /// their groups, the ones they paid for wherever those are, and their own bank's rows.
    /// The same reach <see cref="TransactionService.List"/> has.
    /// </summary>
    private IQueryable<Transaction> Transactions()
    {
        var currentUser = userContext.User;
        var groups = dbContext.Entry(currentUser).Collection(user => user.Groups).Query();

        return dbContext.Set<Transaction>().Where(transaction =>
            groups.Any(group => group.Id == transaction.GroupId) || transaction.UserId == currentUser.Id);
    }

    /// <summary>
    /// The places the caller already has something at.
    /// </summary>
    /// <remarks>
    /// Worked out from the caller's side: the ids their own expenses, their groups' and their
    /// bank's rows name, and only then the merchants with those ids. Asked the other way --
    /// every merchant whose name matches, then whether the caller has anything there -- a
    /// search for "caf" checked ten thousand places one at a time against the expenses table
    /// to find the fifteen that were theirs, and cost the same with or without an index on
    /// the name. This way it is bounded by what one person has, not by every place every
    /// bank has reported.
    /// <para>
    /// Three branches rather than <see cref="Transactions"/>'s one, because its "in my
    /// groups or paid by me" is an OR no single index answers: Postgres read the whole
    /// expenses table for it. Apart, each is an index lookup.
    /// </para>
    /// </remarks>
    private IQueryable<Merchant> Mine()
    {
        var currentUser = userContext.User;
        var userId = currentUser.Id;
        var groups = dbContext.Entry(currentUser).Collection(user => user.Groups).Query().Select(group => group.Id);

        var inMyGroups = dbContext.Set<Transaction>()
            .Where(transaction => groups.Contains(transaction.GroupId))
            .Select(transaction => transaction.MerchantId);

        var paidByMe = dbContext.Set<Transaction>()
            .Where(transaction => transaction.UserId == userId)
            .Select(transaction => transaction.MerchantId);

        var imported = dbContext.Set<BankTransaction>()
            .Where(row => row.Account.Connection.UserId == userId)
            .Select(row => row.MerchantId);

        var places = inMyGroups.Union(paidByMe).Union(imported);

        return dbContext.Set<Merchant>().Where(merchant => places.Contains(merchant.Id));
    }

    /// <summary>
    /// The wire shape, projected in the database. The count is of the caller's own
    /// transactions there, not everybody's: how much other people spend at a place is no
    /// more theirs to learn than which places those are.
    /// </summary>
    private IQueryable<MerchantResponse> Describe(IQueryable<Merchant> merchants)
    {
        var transactions = Transactions();

        return from merchant in merchants
               orderby merchant.Name
               select new MerchantResponse(
                   merchant.Id,
                   merchant.Name,
                   merchant.LogoUrl,
                   merchant.FirstSeenAt,
                   transactions.Count(transaction => transaction.MerchantId == merchant.Id));
    }

    /// <summary>
    /// Whitespace is no logo, and is stored as the null it means: an empty string would
    /// render as a broken image. Anything else has to be somewhere
    /// <see cref="MerchantLogos"/> allows.
    /// </summary>
    private static string? AllowedLogo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!MerchantLogos.IsAllowed(value))
            throw new ValidationException(ErrorCodes.MerchantLogoNotAllowed,
                "A merchant's logo has to come from the bank provider's image hosts.");

        return value.Trim();
    }
}
