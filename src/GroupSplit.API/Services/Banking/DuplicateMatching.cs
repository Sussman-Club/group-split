using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
/// <param name="Confidence">
/// How strong a claim this is. What a caller reads to decide how loudly to say it.
/// </param>
public sealed record DuplicateMatch(
    Expense Expense,
    decimal AmountDifference,
    int DaysApart,
    MatchConfidence Confidence);

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
    /// How far the two amounts may be apart, as a share of the smaller one, for a match
    /// that is <see cref="MatchConfidence.Possible"/>. A tip added after the receipt was
    /// written is the usual reason they differ.
    /// </summary>
    /// <remarks>
    /// This decides nothing on its own any more. A difference inside it also has to have a
    /// shape a duplicate could have -- see
    /// <see cref="TheDifferenceHasAShapeADuplicateCouldHave"/> -- and the two must not name
    /// different places. Measured against real spending, the tolerance alone lets 11.5% of
    /// unrelated pairs through; with those two conditions it is 3.9%.
    /// </remarks>
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
    /// The rule, and the only statement of it: how strongly these two could be the same
    /// money, or null when they could not be.
    /// </summary>
    /// <remarks>
    /// Money of the same currency, inside the date window, and then the amount decides the
    /// grade. Agreeing to the cent is a different claim from being within a quarter, and
    /// treating them alike is what made the suggestion wrong more often than right.
    /// <para>
    /// The payer is not compared here because it is a condition on the *sets* both sides are
    /// drawn from -- the caller's own bank rows against expenses the caller paid -- and
    /// enforced there.
    /// </para>
    /// <para>
    /// The bank's merchant *text* is still not a condition: "Dinner" against
    /// <c>SQ *TRATTORIA 4421</c> is the ordinary case, so a name test would refuse most real
    /// duplicates. What is a condition is the merchant both sides resolved to, which is a
    /// different thing -- see <see cref="NameDifferentPlaces"/>.
    /// </para>
    /// </remarks>
    private static MatchConfidence? Grade(BankTransaction row, Transaction expense)
    {
        var (spent, recorded) = (Facts(row), Facts(expense));

        if (!string.Equals(spent.Currency, recorded.Currency, StringComparison.OrdinalIgnoreCase))
            return null;

        if (DaysBetween(spent, recorded) > WindowDays)
            return null;

        // The same money to the cent. This is the only claim worth putting in front of
        // somebody who has not touched the row, and it stands even against a merchant that
        // disagrees: one shop can end up as two -- a chain and its subscription arm, a shop
        // and its parent -- and refusing a real duplicate is the expensive mistake.
        if (spent.Amount == recorded.Amount)
            return MatchConfidence.Confident;

        if (NameDifferentPlaces(row, expense))
            return null;

        return WithinTolerance(spent, recorded)
               && TheDifferenceHasAShapeADuplicateCouldHave(row, spent, recorded)
            ? MatchConfidence.Possible
            : null;
    }

    /// <summary>Close enough in amount to be worth asking about at all.</summary>
    private static bool WithinTolerance(SpendingFacts row, SpendingFacts expense)
    {
        var difference = Math.Abs(row.Amount - expense.Amount);

        return difference <= AbsoluteTolerance
               || difference <= RelativeTolerance * Math.Min(Math.Abs(row.Amount), Math.Abs(expense.Amount));
    }

    /// <summary>
    /// Whether the direction of the difference is one a duplicate could actually produce.
    /// </summary>
    /// <remarks>
    /// The tolerance exists for a tip, and a tip only ever makes the card charge *larger*
    /// than the figure somebody wrote down. Reading the tolerance both ways cost half of
    /// everything the rule raised -- and both false suggestions the seeded inbox produces are
    /// of the shape this refuses, a bank charging less than the expense it was offered
    /// against.
    /// <para>
    /// Two things do explain a charge coming in lower. A <b>pending</b> authorisation is
    /// often taken before the tip is added, so for a pending row the lower charge is the
    /// expected one. And people round when they type: an expense on a whole unit may be
    /// above the charge and still be the same money.
    /// </para>
    /// </remarks>
    private static bool TheDifferenceHasAShapeADuplicateCouldHave(
        BankTransaction row, SpendingFacts spent, SpendingFacts recorded)
    {
        if (spent.Amount > recorded.Amount)
            return true;

        if (row.Pending)
            return true;

        return recorded.Amount == Math.Truncate(recorded.Amount);
    }

    /// <summary>
    /// Both sides know where the money went, and it is not the same place.
    /// </summary>
    /// <remarks>
    /// The one condition the merchant is allowed to be, and only against a
    /// <see cref="MatchConfidence.Possible"/> match. A streaming subscription offered against
    /// a coffee run is settled by this and by nothing else in the rule.
    /// <para>
    /// One side knowing and the other not says nothing: most expenses somebody types have no
    /// merchant and never will, so silence here is not disagreement.
    /// </para>
    /// </remarks>
    private static bool NameDifferentPlaces(BankTransaction row, Transaction expense) =>
        row.MerchantId is { } place && expense.MerchantId is { } other && place != other;

    /// <summary>
    /// Both sides name the same place. Not a condition -- a ranking signal, and a better one
    /// than a nearer amount, because a coincidence a few cents closer should not outrank the
    /// genuine duplicate with a tip on it.
    /// </summary>
    private static bool NameTheSamePlace(BankTransaction row, Transaction expense) =>
        row.MerchantId is { } place && expense.MerchantId == place;

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
        // Money coming in has no expense to be a duplicate of, and a row that is already an
        // expense is not about to become a second one.
        //
        // Not "waiting", deliberately. What matters here is whether the row can still be
        // filed, and an ignored one can: filing is refused only for a row that is already
        // Filed, so asking only about New rows would let an ignored row through the guard
        // and into a second expense with nothing said.
        var asking = rows
            .Where(row => row.Amount > 0 && row.Status is BankTransactionStatus.New or BankTransactionStatus.Ignored)
            .ToList();

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
            .Select(row => (Row: row, Confidence: Grade(row, expense)))
            .Where(candidate => candidate.Confidence is not null)
            .Select(candidate => new Graded<BankTransaction>(
                candidate.Row,
                candidate.Confidence!.Value,
                NameTheSamePlace(candidate.Row, expense),
                ReadsLike(candidate.Row, expense),
                Facts(candidate.Row)));

        return [.. BestFirst(could, facts).Take(MostSuggestions).Select(candidate => candidate.Value)];
    }

    public async Task Dismiss(BankTransaction row, Expense expense, CancellationToken ct = default)
    {
        var already = await dbContext.Set<BankMatchDismissal>()
            .AnyAsync(dismissal => dismissal.BankTransactionId == row.Id
                                   && dismissal.TransactionId == expense.Id, ct);

        if (already)
            return;

        var dismissal = new BankMatchDismissal
        {
            BankTransactionId = row.Id,
            TransactionId = expense.Id,
            DismissedAt = clock.GetUtcNow()
        };

        dbContext.Add(dismissal);

        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same pair, dismissed twice at once: a double click, or two tabs. The read
            // above cannot close that window, and it does not need to -- the answer being
            // given is the answer already recorded, so this is what success looks like.
            // Detaching the losing row keeps it from being retried by a later save.
            dbContext.Entry(dismissal).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// The candidates ranked and cut to the few worth showing.
    /// </summary>
    private static IReadOnlyList<DuplicateMatch> Ranked(BankTransaction row, IEnumerable<Expense> candidates)
    {
        var facts = Facts(row);

        var could = candidates
            .Select(expense => (Expense: expense, Confidence: Grade(row, expense)))
            .Where(candidate => candidate.Confidence is not null)
            .Select(candidate => new Graded<Expense>(
                candidate.Expense,
                candidate.Confidence!.Value,
                NameTheSamePlace(row, candidate.Expense),
                ReadsLike(row, candidate.Expense),
                Facts(candidate.Expense)));

        return
        [
            .. BestFirst(could, facts)
                .Take(MostSuggestions)
                .Select(candidate => new DuplicateMatch(
                    candidate.Value,
                    Math.Abs(row.Amount - candidate.Value.Amount),
                    DaysBetween(facts, candidate.Facts),
                    candidate.Confidence))
        ];
    }

    /// <summary>
    /// One candidate with everything the order depends on already worked out.
    /// </summary>
    /// <param name="SamePlace">Both sides resolved to the same merchant.</param>
    /// <param name="ReadsAlike">
    /// The bank's own text and the expense's name have something to do with each other. The
    /// weakest of the signals and the last one consulted -- worth something only when
    /// neither side knows the place.
    /// </param>
    private readonly record struct Graded<T>(
        T Value,
        MatchConfidence Confidence,
        bool SamePlace,
        bool ReadsAlike,
        SpendingFacts Facts);

    /// <summary>
    /// Best first: a confident match before a possible one, then the place agreeing, then
    /// nearest in amount, then nearest in date, and only then the bank's own text.
    /// </summary>
    /// <remarks>
    /// The place outranks a nearer amount deliberately. Ordering by closeness alone let a
    /// coincidence a few cents nearer outrank the genuine duplicate with a tip on it -- and
    /// since the inbox leads with the best candidate, that did not merely mis-sort the list,
    /// it kept the real match off the screen.
    /// <para>
    /// The order candidates are offered in whichever side is asking, so the two directions
    /// cannot drift apart.
    /// </para>
    /// </remarks>
    private static IOrderedEnumerable<Graded<T>> BestFirst<T>(IEnumerable<Graded<T>> candidates,
        SpendingFacts against) =>
        candidates
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenByDescending(candidate => candidate.SamePlace)
            .ThenBy(candidate => Math.Abs(candidate.Facts.Amount - against.Amount))
            .ThenBy(candidate => DaysBetween(candidate.Facts, against))
            .ThenByDescending(candidate => candidate.ReadsAlike);

    /// <summary>
    /// Whether the two names have anything to do with each other. The last tiebreak and
    /// worth nothing else: banks write <c>SQ *TRATTORIA 4421</c> where people write "Dinner".
    /// </summary>
    /// <remarks>
    /// Below <see cref="NameTheSamePlace"/>, which asks the same question of the merchant
    /// both sides resolved to and gets a real answer. This is what is left when neither side
    /// has one.
    /// </remarks>
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
    /// date range can be put in; <see cref="Grade"/> decides. A person spends
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
                match.DaysApart,
                match.Confidence);
    }

    extension(IEnumerable<DuplicateMatch> matches)
    {
        public IReadOnlyList<ExpenseMatchResponse> ToResponses() => [.. matches.Select(match => match.ToResponse())];
    }
}
