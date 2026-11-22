using System.Text.Json;
using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.RecurringExpenses;

/// <summary>
/// Expenses that repeat on a schedule (#546): setting one up, changing, pausing and stopping it.
/// <see cref="CreateDueRecurringExpensesHandler"/> creates the occurrences.
/// </summary>
/// <remarks>
/// Any member of the group may change one, as with every other expense in it; members are
/// equal. Whoever last set it up, changed it or resumed it is who the occurrences are entered
/// by and checked as, so one somebody set up before leaving the group starts working again
/// once anybody still in it saves it.
/// </remarks>
public interface IRecurringExpenseService
{
    /// <summary>A group's repeating expenses that have not been stopped, the next due first.</summary>
    Task<List<RecurringExpenseResponse>> InGroup(Guid groupId, CancellationToken ct = default);

    /// <summary>
    /// Sets one up. The first occurrence is <see cref="RecurringExpenseRequest.StartsOn"/>, or
    /// the first one from today when that is in the past: missed days are not filled in.
    /// </summary>
    Task<RecurringExpenseResponse> Create(RecurringExpenseRequest request, CancellationToken ct = default);

    /// <summary>
    /// Repeats an existing expense from the occurrence after it: same group, payer, category,
    /// split and amount, on the same day of the week, month or year.
    /// </summary>
    Task<RecurringExpenseResponse> Repeat(Guid expenseId, RepeatExpenseRequest request, CancellationToken ct = default);

    /// <summary>
    /// Changes the occurrences still to come. The group stays the one it was set up in.
    /// Changing the frequency or the start moves the next occurrence to the first one of the
    /// new schedule from today.
    /// </summary>
    Task<RecurringExpenseResponse> Update(Guid id, RecurringExpenseRequest request, CancellationToken ct = default);

    Task<RecurringExpenseResponse> Pause(Guid id, CancellationToken ct = default);

    /// <summary>Resumes from the next occurrence from today; the paused ones are not created.</summary>
    Task<RecurringExpenseResponse> Resume(Guid id, CancellationToken ct = default);

    /// <summary>Stops it for good. The expenses it already created stay as they are.</summary>
    Task Stop(Guid id, CancellationToken ct = default);
}

internal sealed class RecurringExpenseService(
    ICurrentUser userContext,
    AppDbContext dbContext,
    ITransactionService transactions,
    TimeProvider clock) : IRecurringExpenseService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<List<RecurringExpenseResponse>> InGroup(Guid groupId, CancellationToken ct = default)
    {
        var callerId = userContext.User.Id;
        if (!await dbContext.Set<Group>().AnyAsync(group => group.Id == groupId && group.Users.Any(u => u.Id == callerId), ct))
        {
            throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");
        }

        var rows = await dbContext.Set<RecurringExpense>().AsNoTracking()
            .Where(recurring => recurring.GroupId == groupId && recurring.StoppedAt == null)
            .OrderBy(recurring => recurring.NextOn)
            .ToListAsync(ct);

        return await Describe(rows, ct);
    }

    public async Task<RecurringExpenseResponse> Create(RecurringExpenseRequest request, CancellationToken ct = default)
    {
        var template = await Checked(request with { GroupId = request.GroupId ?? userContext.User.Id }, ct);

        var recurring = new RecurringExpense
        {
            GroupId = template.GroupId!.Value,
            EnteredById = userContext.User.Id,
            Template = JsonSerializer.Serialize(template, Json),
            Frequency = template.Frequency,
            StartsOn = template.StartsOn,
            NextOn = Recurrence.OnOrAfter(template.StartsOn, template.Frequency, Today),
            CreatedAt = clock.GetUtcNow()
        };

        dbContext.Add(recurring);
        await dbContext.SaveChangesAsync(ct);

        return (await Describe([recurring], ct))[0];
    }

    public async Task<RecurringExpenseResponse> Repeat(Guid expenseId, RepeatExpenseRequest request,
        CancellationToken ct = default)
    {
        var expense = await (await transactions.Get(expenseId, ct))
                          .Include(e => e.SplitRuleVersion)
                          .Include(e => e.Category)
                          .Include(e => e.Group)
                          .Include(e => e.Splits)
                          .FirstOrDefaultAsync(ct)
                      ?? throw new NotFoundException(ErrorCodes.TransactionNotFound, "Transaction not found.");

        var ruleId = expense.SplitRuleVersion?.SplitRuleId;
        var usualRuleId = expense.Category is { } category ? category.DefaultSplitRuleId : expense.Group.DefaultSplitRuleId;
        var template = new RecurringExpenseRequest
        {
            GroupId = expense.GroupId,
            PaidByUserId = expense.UserId,
            CategoryId = expense.CategoryId,
            MerchantId = expense.MerchantId,
            // The rule the category (or, without one, the group) divides by anyway is left to it,
            // so changing that rule later changes the occurrences too, as it would an expense
            // typed in then.
            SplitRuleId = ruleId == usualRuleId ? null : ruleId,
            Name = expense.Name,
            Description = expense.Description,
            Currency = expense.Currency,
            Amount = expense.Amount,
            Frequency = request.Frequency,
            StartsOn = request.From ?? DateOnly.FromDateTime(expense.DateTime.UtcDateTime)
        };

        // Shares somebody typed in are kept as they were typed. Ones the group's usual division
        // gave are left to it, so a member who joins later is included like on any new expense.
        if (template.SplitRuleId is null && !expense.IsPersonal)
        {
            var usual = await transactions.Preview(template.On(template.StartsOn), ct);
            var stored = expense.Splits.ToDictionary(split => split.UserId, split => split.Amount);
            var same = usual.Splits.Count == stored.Count &&
                       usual.Splits.All(split => stored.TryGetValue(split.UserId, out var amount) && amount == split.Amount);
            if (!same)
            {
                template.Splits = expense.Splits.Select(split => new SplitInput { UserId = split.UserId, Amount = split.Amount })
                    .ToList();
            }
        }

        var recurring = new RecurringExpense
        {
            GroupId = expense.GroupId,
            EnteredById = userContext.User.Id,
            Template = JsonSerializer.Serialize(await Checked(template, ct), Json),
            Frequency = template.Frequency,
            StartsOn = template.StartsOn,
            // The expense itself is the first occurrence.
            NextOn = Recurrence.OnOrAfter(template.StartsOn, template.Frequency,
                Max(Recurrence.After(template.StartsOn, template.Frequency, template.StartsOn), Today)),
            CreatedAt = clock.GetUtcNow()
        };

        dbContext.Add(recurring);
        await dbContext.SaveChangesAsync(ct);

        return (await Describe([recurring], ct))[0];
    }

    public async Task<RecurringExpenseResponse> Update(Guid id, RecurringExpenseRequest request,
        CancellationToken ct = default)
    {
        var recurring = await Existing(id, ct);
        var template = await Checked(request with { GroupId = recurring.GroupId }, ct);

        if (template.Frequency != recurring.Frequency || template.StartsOn != recurring.StartsOn)
        {
            recurring.Frequency = template.Frequency;
            recurring.StartsOn = template.StartsOn;
            recurring.NextOn = Recurrence.OnOrAfter(template.StartsOn, template.Frequency, Today);
        }

        recurring.Template = JsonSerializer.Serialize(template, Json);
        recurring.EnteredById = userContext.User.Id;
        // Checked just now as the person who will enter it, so whatever stopped it is fixed.
        recurring.Problem = null;
        await dbContext.SaveChangesAsync(ct);

        return (await Describe([recurring], ct))[0];
    }

    public async Task<RecurringExpenseResponse> Pause(Guid id, CancellationToken ct = default)
    {
        var recurring = await Existing(id, ct);
        recurring.PausedAt ??= clock.GetUtcNow();
        await dbContext.SaveChangesAsync(ct);

        return (await Describe([recurring], ct))[0];
    }

    public async Task<RecurringExpenseResponse> Resume(Guid id, CancellationToken ct = default)
    {
        var recurring = await Existing(id, ct);
        if (recurring.PausedAt is not null)
        {
            recurring.PausedAt = null;
            recurring.NextOn = Recurrence.OnOrAfter(recurring.StartsOn, recurring.Frequency, Max(recurring.NextOn, Today));
            recurring.EnteredById = userContext.User.Id;
            recurring.Problem = null;
            await dbContext.SaveChangesAsync(ct);
        }

        return (await Describe([recurring], ct))[0];
    }

    public async Task Stop(Guid id, CancellationToken ct = default)
    {
        var recurring = await Existing(id, ct);
        recurring.StoppedAt = clock.GetUtcNow();
        await dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The template with the payer filled in, once a first occurrence of it has been divided
    /// as the caller: what would be refused on its day is refused now, with the same code.
    /// </summary>
    private async Task<RecurringExpenseRequest> Checked(RecurringExpenseRequest request, CancellationToken ct)
    {
        var template = request with { PaidByUserId = request.PaidByUserId ?? userContext.User.Id };
        await transactions.Preview(template.On(template.StartsOn), ct);
        return template;
    }

    private async Task<RecurringExpense> Existing(Guid id, CancellationToken ct)
    {
        var callerId = userContext.User.Id;
        return await dbContext.Set<RecurringExpense>()
                   .FirstOrDefaultAsync(recurring => recurring.Id == id && recurring.StoppedAt == null &&
                                                     recurring.Group.Users.Any(u => u.Id == callerId), ct)
               // Another group's says what a missing one says.
               ?? throw new NotFoundException(ErrorCodes.RecurringExpenseNotFound, "Repeating expense not found.");
    }

    private async Task<List<RecurringExpenseResponse>> Describe(IReadOnlyList<RecurringExpense> rows,
        CancellationToken ct)
    {
        var templates = rows.Select(row => JsonSerializer.Deserialize<RecurringExpenseRequest>(row.Template, Json)!).ToList();
        var payerIds = templates.Select(template => template.PaidByUserId).OfType<Guid>().Distinct().ToList();
        var payers = await dbContext.Set<User>().AsNoTracking()
            .Where(user => payerIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, People.Display, ct);

        return rows.Select((row, i) => new RecurringExpenseResponse(
                row.Id,
                row.GroupId,
                templates[i] with { Frequency = row.Frequency, StartsOn = row.StartsOn },
                templates[i].PaidByUserId is { } payer && payers.TryGetValue(payer, out var name) ? name : "",
                row.EnteredById,
                row.NextOn,
                row.PausedAt is not null,
                row.Problem))
            .ToList();
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
