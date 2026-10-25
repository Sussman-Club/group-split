using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Services.Banking.Suggestions;

/// <summary>
/// Suggests a group and category from spending facts supplied by any entry flow.
/// </summary>
public interface IFilingSuggester
{
    Task<FilingDestinationSuggestion?> SuggestReceipt(SuggestReceiptFilingRequest request, CancellationToken ct = default);
    /// <summary>
    /// A suggestion for each row that gets one. Rows the model abstains on, or is not
    /// confident enough about, are simply not in the answer.
    /// </summary>
    /// <remarks>
    /// Never throws for the model's sake: a call that fails is an abstention, so the inbox
    /// looks the way it did before suggestions existed rather than failing to load.
    /// </remarks>
    Task<IReadOnlyList<FilingSuggestionResult>> Suggest(
        IReadOnlyList<(FilingInput Row, FilingReceipt? Receipt)> rows,
        CancellationToken ct = default);
}

/// <summary>
/// A discriminator, not a generator: the model is given the row, the receipt where there
/// is one, the caller's own groups and categories, and what earlier charges from the same
/// merchant became, and it picks one of those destinations or says it cannot tell.
/// </summary>
/// <remarks>
/// Every option it is offered is one the caller can file into -- a category that is not
/// archived, in a group they belong to whose currency takes the row's -- and an answer
/// that is not one of them is dropped. Member names never leave the server: what is sent is
/// the merchant, the amounts, the bank's labels, the receipt's item names, and the group
/// and category names.
/// </remarks>
public sealed class FilingSuggester(
    ICurrentUser userContext,
    IGroupService groups,
    AppDbContext dbContext,
    IDecisionClient decisions,
    HybridCache cache,
    IOptions<FilingSuggestionOptions> options,
    ILogger<FilingSuggester> logger) : IFilingSuggester
{
    /// <summary>The option that lets the model abstain, as the service recommends.</summary>
    internal const string CannotTell = "cannot tell";

    /// <summary>One more than this is the most options a question may have.</summary>
    private const int MaxDestinations = 254;

    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromDays(7),
        LocalCacheExpiration = TimeSpan.FromDays(7)
    };

    internal const string Instructions =
        "This is one expense belonging to the account holder. Which of their own "
        + "groups and categories should it be filed under? Earlier filings of the same merchant "
        + "and the receipt, when given, are the strongest evidence. If nothing fits, or the "
        + "evidence does not point anywhere, answer \"cannot tell\".";

    public async Task<IReadOnlyList<FilingSuggestionResult>> Suggest(
        IReadOnlyList<(FilingInput Row, FilingReceipt? Receipt)> rows,
        CancellationToken ct = default)
    {
        var settings = options.Value;

        if (!settings.IsConfigured || rows.Count == 0)
            return [];

        // Everything from the database first, in sequence: the context is not one to share
        // between the calls below, which are the slow part and go out together.
        var destinations = await DestinationsAsync(ct);

        if (destinations.Count == 0)
            return [];

        var history = await HistoryAsync(rows.Select(item => item.Row), destinations, settings.HistoryLimit, ct);

        var answers = new FilingDestinationSuggestion?[rows.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, rows.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, settings.Concurrency), CancellationToken = ct },
            async (index, token) =>
            {
                var (row, receipt) = rows[index];
                var earlier = row.MerchantId is { } merchantId
                    ? history.GetValueOrDefault(merchantId) ?? []
                    : [];

                answers[index] = await SuggestOne(row, receipt, destinations, earlier, settings, token);
            });

        return [.. answers.Select((answer, index) => answer is null ? null : new FilingSuggestionResult(rows[index].Row.Id, answer)).OfType<FilingSuggestionResult>()];
    }

    public async Task<FilingDestinationSuggestion?> SuggestReceipt(SuggestReceiptFilingRequest request, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
            return null;
        var destinations = await DestinationsAsync(ct);
        if (request.GroupId is { } groupId)
            destinations = destinations.Where(destination => destination.GroupId == groupId).ToList();
        if (destinations.Count == 0)
            return null;

        var row = new FilingInput(Guid.Empty, request.Receipt.PurchaseDate, request.Receipt.Total,
            request.Currency, request.Receipt.MerchantName ?? "Receipt", request.Receipt.MerchantName);
        var receipt = new FilingReceipt
        {
            MerchantName = request.Receipt.MerchantName,
            Items = request.Receipt.Items.Select(item => item.Name).Take(FilingReceipt.MaxItems).ToArray()
        };
        return await SuggestOne(row, receipt, destinations, [], settings, ct);
    }

    private async Task<FilingDestinationSuggestion?> SuggestOne(
        FilingInput row,
        FilingReceipt? receipt,
        IReadOnlyList<Destination> destinations,
        IReadOnlyList<Destination> earlier,
        FilingSuggestionOptions settings,
        CancellationToken ct)
    {
        // A group's balances are one currency, so a row in another one cannot be filed
        // there; the personal group takes any.
        var fitting = destinations
            .Where(destination => destination.IsPersonal
                                  || string.Equals(destination.Currency, row.Currency, StringComparison.OrdinalIgnoreCase))
            // What this merchant went to before is likeliest, so it is never what gets cut
            // when somebody has more categories than one question can carry.
            .OrderByDescending(destination => earlier.Count(filed => filed.CategoryId == destination.CategoryId))
            .Take(MaxDestinations)
            .ToList();

        if (fitting.Count == 0)
            return null;

        var labelled = Label(fitting);

        var choices = labelled.ToDictionary(pair => pair.Key, pair => pair.Value.Description);
        choices[CannotTell] = "None of these fits, or there is not enough to go on.";

        var key = CacheKey(row, receipt, destinations, fitting, earlier, settings);
        var modelWasCalled = false;

        try
        {
            var cached = await cache.GetOrCreateAsync(
                key,
                async token =>
                {
                    modelWasCalled = true;
                    return new CachedSuggestion(await AskAsync(row, receipt, earlier, labelled, choices, settings, token));
                },
                CacheOptions,
                cancellationToken: ct);

            // A hit deliberately creates no Decision-1 span: no request left this service.
            Activity.Current?.SetTag("groupsplit.filing_suggestion.cache_hit", !modelWasCalled);
            return cached.Value;
        }
        catch (DecisionFailedException)
        {
            return null;
        }
    }

    /// <summary>Calls the classifier. A failure is not a cacheable abstention.</summary>
    private async Task<FilingDestinationSuggestion?> AskAsync(
        FilingInput row,
        FilingReceipt? receipt,
        IReadOnlyList<Destination> earlier,
        IReadOnlyDictionary<string, Destination> labelled,
        IReadOnlyDictionary<string, string> choices,
        FilingSuggestionOptions settings,
        CancellationToken ct)
    {
        DecisionAnswer? answer;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(settings.Timeout);

            answer = await decisions.ChooseAsync(State(row, receipt, earlier), Instructions, choices, timeout.Token);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException
            && !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Filing suggestion for spending input {SourceId} failed; none given.", row.Id);
            throw new DecisionFailedException();
        }

        if (answer is null
            || answer.Confidence < settings.Threshold
            || !labelled.TryGetValue(answer.Choice, out var chosen))
        {
            return null;
        }

        return new FilingDestinationSuggestion(
            chosen.GroupId,
            chosen.GroupName,
            chosen.IsPersonal,
            chosen.CategoryId,
            chosen.CategoryName,
            answer.Confidence);
    }

    /// <summary>
    /// Hashes precisely what can change the answer, without putting private merchant or
    /// receipt details into Redis keys. Each change naturally names a fresh cache entry.
    /// </summary>
    private static string CacheKey(
        FilingInput row,
        FilingReceipt? receipt,
        IReadOnlyList<Destination> allDestinations,
        IReadOnlyList<Destination> destinations,
        IReadOnlyList<Destination> earlier,
        FilingSuggestionOptions settings)
    {
        var inputs = JsonSerializer.SerializeToUtf8Bytes(new
        {
            input = row,
            // The entire caller-visible set participates too. A newly added category gets a
            // fresh answer even if the request's size cap would not offer it this time.
            allDestinations = allDestinations.Select(destination => new
            {
                destination.GroupId,
                destination.GroupName,
                destination.GroupDescription,
                destination.IsPersonal,
                destination.Currency,
                destination.CategoryId,
                destination.CategoryName,
                destination.CategoryDescription
            }),
            destinations = destinations.Select(destination => new
            {
                destination.GroupId,
                destination.GroupName,
                destination.GroupDescription,
                destination.IsPersonal,
                destination.Currency,
                destination.CategoryId,
                destination.CategoryName,
                destination.CategoryDescription
            }),
            earlier = earlier.Select(destination => new { destination.GroupId, destination.CategoryId }),
            receipt = receipt is null ? null : new { receipt.MerchantName, receipt.Items },
            settings.Deployment,
            settings.Threshold
        });

        return "filing-suggestion:v2:" + Convert.ToHexString(SHA256.HashData(inputs));
    }

    /// <summary>
    /// What the model judges. Only facts about the money and where it could go; nobody's
    /// name but the merchant's.
    /// </summary>
    internal static object State(FilingInput row, FilingReceipt? receipt, IReadOnlyList<Destination> earlier) => new
    {
        merchant = Merchant(row),
        bankDescription = row.Description,
        amount = row.Amount,
        currency = row.Currency,
        date = row.Date?.ToString("yyyy-MM-dd"),
        bankCategory = row.ProviderCategory,
        bankCategoryDetailed = row.ProviderCategoryDetailed,
        account = row.AccountName,
        paymentChannel = row.PaymentChannel,
        city = row.City,
        earlierFilingsOfThisMerchant = earlier.Count == 0
            ? null
            : earlier
                .GroupBy(filed => filed.Label)
                .OrderByDescending(same => same.Count())
                .Select(same => $"{same.Key}: {same.Count()} of the last {earlier.Count}")
                .ToList(),
        receipt = receipt is null
            ? null
            : new { merchant = receipt.MerchantName, items = receipt.Items }
    };

    private static string Merchant(FilingInput row) =>
        string.IsNullOrWhiteSpace(row.MerchantName) ? row.Description : row.MerchantName;

    /// <summary>
    /// Options keyed by what the model reads. Two groups can share a name, so a label that
    /// is already taken gets a number rather than silently standing for both.
    /// </summary>
    private static Dictionary<string, Destination> Label(IEnumerable<Destination> destinations)
    {
        var labelled = new Dictionary<string, Destination>(StringComparer.Ordinal);

        foreach (var destination in destinations)
        {
            var label = destination.Label;

            for (var n = 2; labelled.ContainsKey(label) || label == CannotTell; n++)
                label = $"{destination.Label} ({n})";

            labelled[label] = destination;
        }

        return labelled;
    }

    /// <summary>Every category the caller can file into, with the group it is in.</summary>
    private async Task<IReadOnlyList<Destination>> DestinationsAsync(CancellationToken ct)
    {
        var me = userContext.User.Id;
        var mine = (await groups.GetAllGroups(ct)).Select(group => group.Id);

        var found = await dbContext.Set<Category>()
            .Where(category => category.ArchivedAt == null && mine.Contains(category.Group.Id))
            .OrderBy(category => category.Group.Name)
            .ThenBy(category => category.Name)
            .Select(category => new
            {
                GroupId = category.Group.Id,
                GroupName = category.Group.Name,
                GroupDescription = category.Group.Description,
                category.Group.Currency,
                CategoryId = category.Id,
                CategoryName = category.Name,
                CategoryDescription = category.Description
            })
            .ToListAsync(ct);

        return [.. found.Select(category => new Destination(
            category.GroupId,
            category.GroupId == me ? "Personal" : category.GroupName,
            category.GroupDescription,
            category.GroupId == me,
            category.Currency,
            category.CategoryId,
            category.CategoryName,
            category.CategoryDescription))];
    }

    /// <summary>
    /// What the caller's earlier charges from each of these merchants became, newest first,
    /// counting only the ones that landed somewhere still on offer.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Destination>>> HistoryAsync(
        IEnumerable<FilingInput> rows,
        IReadOnlyList<Destination> destinations,
        int limit,
        CancellationToken ct)
    {
        var merchants = rows.Select(row => row.MerchantId).OfType<Guid>().Distinct().ToList();

        if (merchants.Count == 0 || limit <= 0)
            return new Dictionary<Guid, IReadOnlyList<Destination>>();

        var me = userContext.User.Id;

        var filed = await dbContext.Set<Expense>()
            .Where(expense => expense.UserId == me
                              && expense.MerchantId != null
                              && merchants.Contains(expense.MerchantId.Value)
                              && expense.CategoryId != null)
            .OrderByDescending(expense => expense.DateTime)
            .Select(expense => new { MerchantId = expense.MerchantId!.Value, CategoryId = expense.CategoryId!.Value })
            .Take(merchants.Count * limit * 4)
            .ToListAsync(ct);

        var byCategory = destinations.ToDictionary(destination => destination.CategoryId);

        return filed
            .Where(expense => byCategory.ContainsKey(expense.CategoryId))
            .GroupBy(expense => expense.MerchantId)
            .ToDictionary(
                merchant => merchant.Key,
                IReadOnlyList<Destination> (merchant) => [.. merchant
                    .Take(limit)
                    .Select(expense => byCategory[expense.CategoryId])]);
    }

    /// <summary>One group-and-category pair a row could be filed under.</summary>
    internal sealed record Destination(
        Guid GroupId,
        string GroupName,
        string GroupDescription,
        bool IsPersonal,
        string Currency,
        Guid CategoryId,
        string CategoryName,
        string CategoryDescription)
    {
        public string Label => $"{GroupName} · {CategoryName}";

        public string Description => (IsPersonal
            ? $"The account holder's own expenses, category \"{CategoryName}\". Nobody else shares it."
            : $"The shared group \"{GroupName}\", category \"{CategoryName}\".")
            + OptionalDescription("Group", GroupDescription)
            + OptionalDescription("Category", CategoryDescription);

        private static string OptionalDescription(string kind, string description) =>
            string.IsNullOrWhiteSpace(description) ? string.Empty : $" {kind}: \"{description}\".";
    }

    private sealed record CachedSuggestion(FilingDestinationSuggestion? Value);

    private sealed class DecisionFailedException : Exception;
}
