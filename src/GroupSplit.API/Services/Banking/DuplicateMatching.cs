using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// One spending event reduced to the three things both sides of a match actually agree on.
/// </summary>
/// <param name="On">
/// The day the money was spent: the authorised date where the provider knows one, the
/// posting date otherwise, and for an expense the day it is recorded against.
/// </param>
public sealed record SpendingFacts(decimal Amount, string Currency, DateOnly On);

/// <summary>
/// An expense that could already be the same money as an imported row, and how close it is.
/// </summary>
/// <param name="AmountDifference">
/// Always zero or more. A card can settle for more than the receipt -- a tip added
/// afterwards -- so a difference is expected rather than disqualifying.
/// </param>
/// <param name="DaysApart">
/// How far apart the two dates are. A card charge posts days after the meal.
/// </param>
public sealed record DuplicateMatch(Expense Expense, decimal AmountDifference, int DaysApart);

/// <summary>
/// Whether an imported row and an expense could be the same money.
/// </summary>
/// <remarks>
/// Everything that decides it is in <see cref="DuplicateMatcher"/> and nowhere else, so the
/// window and the tolerance can be tuned in one file without hunting for callers that
/// assumed the old numbers. Callers get candidates and say what a person chose; they never
/// ask what the rule is.
/// <para>
/// Nothing here writes to the ledger. A match is a suggestion, always: it is the person who
/// files, links or dismisses.
/// </para>
/// </remarks>
public interface IDuplicateMatcher
{
    /// <summary>
    /// The caller's expenses that could already be <paramref name="row"/>, best first.
    /// Empty when there is nothing worth raising.
    /// </summary>
    Task<IReadOnlyList<DuplicateMatch>> ExpensesLike(BankTransaction row, CancellationToken ct = default);

    /// <summary>
    /// The same question for a page of rows at once, keyed by row id. One pass over the
    /// candidates rather than one query per row, because this runs on every inbox listing.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<DuplicateMatch>>> ExpensesLike(
        IReadOnlyCollection<BankTransaction> rows, CancellationToken ct = default);

    /// <summary>
    /// The other order: rows still waiting in the caller's inbox that could already be
    /// <paramref name="expense"/>. What is asked right after somebody records one by hand.
    /// </summary>
    Task<IReadOnlyList<BankTransaction>> RowsLike(Expense expense, CancellationToken ct = default);

    /// <summary>
    /// Records that a person said these two are not the same money, so the pair is never
    /// raised again. Dismissing an already dismissed pair changes nothing.
    /// </summary>
    Task Dismiss(BankTransaction row, Expense expense, CancellationToken ct = default);
}

/// <inheritdoc cref="IDuplicateMatcher"/>
public sealed class DuplicateMatcher(
    ICurrentUser userContext,
    AppDbContext dbContext,
    TimeProvider clock) : IDuplicateMatcher
{
    /// <summary>
    /// How many days either way still counts as the same spending.
    /// </summary>
    /// <remarks>
    /// A card charge posts days after the meal, and a weekend one can wait until Tuesday.
    /// A window that only caught the same day would miss most real duplicates, which is the
    /// whole problem this exists for.
    /// </remarks>
    private const int WindowDays = 5;

    /// <summary>
    /// How far the two amounts may be apart, as a share of the smaller one. A tip added
    /// after the receipt was written is the usual reason they differ.
    /// </summary>
    private const decimal RelativeTolerance = 0.25m;

    /// <summary>
    /// The same, as a flat amount, so small sums are not held to a tolerance of pennies.
    /// Whichever of the two is more generous applies.
    /// </summary>
    private const decimal AbsoluteTolerance = 1.00m;

    /// <summary>
    /// How many suggestions one row is worth raising. A person deciding between five
    /// candidates is not being helped by any of them.
    /// </summary>
    private const int MostSuggestions = 3;

    /// <summary>
    /// The rule, and the only statement of it.
    /// </summary>
    /// <remarks>
    /// Amount and a date window, on money of the same currency. The payer is not compared
    /// here because it is a condition on the *sets* both sides are drawn from -- the
    /// caller's own bank rows against expenses the caller paid -- and enforced there.
    /// <para>
    /// The bank's merchant text is deliberately not consulted. "Dinner" against
    /// <c>SQ *TRATTORIA 4421</c> is the ordinary case, so a name condition would refuse
    /// most real duplicates; it is a tiebreak in <see cref="Ranked"/> at most.
    /// </para>
    /// </remarks>
    private static bool CouldBeTheSameMoney(SpendingFacts row, SpendingFacts expense)
    {
        if (!string.Equals(row.Currency, expense.Currency, StringComparison.OrdinalIgnoreCase))
            return false;

        if (DaysBetween(row, expense) > WindowDays)
            return false;

        var difference = Math.Abs(row.Amount - expense.Amount);

        return difference <= AbsoluteTolerance
               || difference <= RelativeTolerance * Math.Min(Math.Abs(row.Amount), Math.Abs(expense.Amount));
    }

    private static int DaysBetween(SpendingFacts row, SpendingFacts expense) =>
        Math.Abs(row.On.DayNumber - expense.On.DayNumber);

    /// <summary>What a bank row says about the money, for the rule above.</summary>
    private static SpendingFacts Facts(BankTransaction row) =>
        new(row.Amount, row.Currency, row.AuthorizedDate ?? row.Date);

    /// <summary>The same, for an expense. Stored as an instant; compared as the day it fell on.</summary>
    private static SpendingFacts Facts(Transaction expense) =>
        new(expense.Amount, expense.Currency, DateOnly.FromDateTime(expense.DateTime.UtcDateTime));

    public async Task<IReadOnlyList<DuplicateMatch>> ExpensesLike(BankTransaction row, CancellationToken ct = default)
    {
        var byRow = await ExpensesLike([row], ct);

        return byRow.GetValueOrDefault(row.Id, []);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DuplicateMatch>>> ExpensesLike(
        IReadOnlyCollection<BankTransaction> rows, CancellationToken ct = default)
    {
        // Money coming in has no expense to be a duplicate of, and a row already dealt with
        // is not about to become a second expense.
        var asking = rows.Where(row => row.Amount > 0 && row.Status == BankTransactionStatus.New).ToList();

        if (asking.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<DuplicateMatch>>();

        var days = asking.Select(row => Facts(row).On).ToList();

        var candidates = await Candidates(days.Min(), days.Max(), ct);

        if (candidates.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<DuplicateMatch>>();

        var dismissed = await Dismissed(asking.Select(row => row.Id).ToList(), ct);

        var matches = new Dictionary<Guid, IReadOnlyList<DuplicateMatch>>();

        foreach (var row in asking)
        {
            var found = Ranked(row, candidates.Where(expense =>
                !dismissed.Contains((row.Id, expense.Id))));

            if (found.Count > 0)
                matches[row.Id] = found;
        }

        return matches;
    }

    public async Task<IReadOnlyList<BankTransaction>> RowsLike(Expense expense, CancellationToken ct = default)
    {
        // An expense somebody else paid cannot be one of the caller's card charges, and the
        // caller only ever sees their own bank rows.
        if (expense.UserId != userContext.User.Id || expense.BankTransactionId is not null)
            return [];

        var facts = Facts(expense);

        var rows = await Waiting(facts.On, facts.On, ct);

        if (rows.Count == 0)
            return [];

        var dismissed = await Dismissed(rows.Select(row => row.Id).ToList(), ct);

        var could = rows
            .Where(row => !dismissed.Contains((row.Id, expense.Id)))
            .Where(row => CouldBeTheSameMoney(Facts(row), facts));

        return [.. Closest(could, facts, Facts).Take(MostSuggestions)];
    }

    public async Task Dismiss(BankTransaction row, Expense expense, CancellationToken ct = default)
    {
        var already = await dbContext.Set<BankMatchDismissal>()
            .AnyAsync(dismissal => dismissal.BankTransactionId == row.Id
                                   && dismissal.TransactionId == expense.Id, ct);

        if (already)
            return;

        dbContext.Add(new BankMatchDismissal
        {
            BankTransactionId = row.Id,
            TransactionId = expense.Id,
            DismissedAt = clock.GetUtcNow()
        });

        await dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The candidates ranked and cut to the few worth showing: closest amount first, then
    /// nearest date, and only then the bank's own text, which is a tiebreak and never a
    /// condition.
    /// </summary>
    private static IReadOnlyList<DuplicateMatch> Ranked(BankTransaction row, IEnumerable<Expense> candidates)
    {
        var facts = Facts(row);

        var could = candidates.Where(expense => CouldBeTheSameMoney(facts, Facts(expense)));

        return
        [
            .. Closest(could, facts, Facts)
                .ThenByDescending(expense => ReadsLike(row, expense))
                .Take(MostSuggestions)
                .Select(expense => new DuplicateMatch(
                    expense,
                    Math.Abs(row.Amount - expense.Amount),
                    DaysBetween(facts, Facts(expense))))
        ];
    }

    /// <summary>
    /// Closest first: nearest in amount, then nearest in date. The order candidates are
    /// offered in whichever side is asking, so the two directions cannot drift apart.
    /// </summary>
    private static IOrderedEnumerable<T> Closest<T>(IEnumerable<T> candidates, SpendingFacts against,
        Func<T, SpendingFacts> factsOf) =>
        candidates
            .OrderBy(candidate => Math.Abs(factsOf(candidate).Amount - against.Amount))
            .ThenBy(candidate => DaysBetween(factsOf(candidate), against));

    /// <summary>
    /// Whether the two names have anything to do with each other. Worth a tiebreak between
    /// two equally close candidates and worth nothing else: banks write
    /// <c>SQ *TRATTORIA 4421</c> where people write "Dinner".
    /// </summary>
    private static bool ReadsLike(BankTransaction row, Expense expense)
    {
        if (row.MerchantName is not { Length: > 0 } merchant || expense.Name.Length == 0)
            return false;

        return merchant.Contains(expense.Name, StringComparison.OrdinalIgnoreCase)
               || expense.Name.Contains(merchant, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The expenses that could be in play for days between <paramref name="from"/> and
    /// <paramref name="to"/>: the caller's own, not already filed from a bank row, in the
    /// window either side.
    /// </summary>
    /// <remarks>
    /// Deliberately wider than the rule and asked of the database in the plainest terms a
    /// date range can be put in; <see cref="CouldBeTheSameMoney"/> decides. A person spends
    /// a handful of times a day, so this is a small list however it is filtered, and the
    /// arithmetic that matters stays somewhere it can be read.
    /// </remarks>
    private Task<List<Expense>> Candidates(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (after, before) = Window(from, to);
        var me = userContext.User.Id;

        return dbContext.Set<Expense>()
            .Where(expense => expense.UserId == me
                              && expense.BankTransactionId == null
                              && expense.DateTime >= after
                              && expense.DateTime < before)
            .Include(expense => expense.Group)
            .Include(expense => expense.User)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>
    /// The rows still waiting in the caller's inbox over the same window. Ignored and filed
    /// rows are not waiting for anything, and a credit is not an expense.
    /// </summary>
    private Task<List<BankTransaction>> Waiting(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (after, before) = Window(from, to);
        var me = userContext.User.Id;

        // The same day the rule reads -- authorised where there is one, posted otherwise --
        // and not the posting date alone. A charge authorised on the day of the meal can
        // post a week later, which is exactly the row this has to find.
        var afterDay = DateOnly.FromDateTime(after.UtcDateTime);
        var beforeDay = DateOnly.FromDateTime(before.UtcDateTime);

        return dbContext.Set<BankTransaction>()
            .Where(row => row.Account.Connection.UserId == me
                          && row.Status == BankTransactionStatus.New
                          && row.Amount > 0
                          && (row.AuthorizedDate ?? row.Date) >= afterDay
                          && (row.AuthorizedDate ?? row.Date) < beforeDay)
            .Include(row => row.Account)
            .ThenInclude(account => account.Connection)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>
    /// The window either side of a span of days, as the half-open instant range a stored
    /// <see cref="DateTimeOffset"/> is compared against.
    /// </summary>
    private static (DateTimeOffset After, DateTimeOffset Before) Window(DateOnly from, DateOnly to) =>
        (new DateTimeOffset(from.AddDays(-WindowDays).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new DateTimeOffset(to.AddDays(WindowDays + 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    /// <summary>The pairs a person has already said no to, among the rows being asked about.</summary>
    private async Task<HashSet<(Guid Row, Guid Expense)>> Dismissed(IReadOnlyList<Guid> rowIds, CancellationToken ct)
    {
        var pairs = await dbContext.Set<BankMatchDismissal>()
            .Where(dismissal => rowIds.Contains(dismissal.BankTransactionId))
            .Select(dismissal => new { dismissal.BankTransactionId, dismissal.TransactionId })
            .ToListAsync(ct);

        return [.. pairs.Select(pair => (pair.BankTransactionId, pair.TransactionId))];
    }
}

/// <summary>
/// A match on the wire. Here rather than beside the endpoints because two of them and the
/// refusal that filing raises all say the same thing, and saying it once is the point.
/// </summary>
public static class DuplicateMatchExtensions
{
    extension(DuplicateMatch match)
    {
        public ExpenseMatchResponse ToResponse() =>
            new(match.Expense.Id,
                match.Expense.Name,
                match.Expense.Amount,
                match.Expense.Currency,
                match.Expense.DateTime,
                match.Expense.GroupId,
                match.Expense.Group?.Name,
                $"{match.Expense.User.FirstName} {match.Expense.User.LastName}".Trim(),
                match.AmountDifference,
                match.DaysApart);
    }

    extension(IEnumerable<DuplicateMatch> matches)
    {
        public IReadOnlyList<ExpenseMatchResponse> ToResponses() => [.. matches.Select(match => match.ToResponse())];
    }
}
