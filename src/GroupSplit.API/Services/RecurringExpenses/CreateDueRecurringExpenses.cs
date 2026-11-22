using System.Text.Json;
using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.RecurringExpenses;

/// <summary>
/// Create every occurrence of a repeating expense whose day has come. Runs every hour.
/// </summary>
public sealed record CreateDueRecurringExpenses : IJob;

/// <remarks>
/// <para>
/// Each occurrence goes through <see cref="ITransactionService.Create(CreateTransactionRequest, TransactionSource, CancellationToken)"/>
/// as the person the repeating expense is entered by, in a scope of its own, so it is checked
/// exactly as an expense they typed in would be. Moving <see cref="RecurringExpense.NextOn"/>
/// on is saved in the same save as the expense, and <see cref="RecurringExpense.NextOn"/> is a
/// concurrency token: a day is created once however often, or however many copies of, this
/// runs.
/// </para>
/// <para>
/// A day that cannot be created -- the payer has left, the rule no longer applies -- is not
/// skipped. Its reason is kept in <see cref="RecurringExpense.Problem"/> for the group to see,
/// and it is tried again every hour, so fixing the cause creates it, on its own day, and the
/// ones after it. Days missed while the server was down are created the same way.
/// </para>
/// </remarks>
internal sealed class CreateDueRecurringExpensesHandler(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<CreateDueRecurringExpensesHandler> logger) : IJobHandler<CreateDueRecurringExpenses>
{
    internal static readonly JobSchedule.CronSchedule Schedule = JobSchedule.Cron("7 * * * *", TimeZoneInfo.Utc);

    private const int ProblemLength = 512;

    public async ValueTask HandleAsync(CreateDueRecurringExpenses job, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<RecurringExpense>()
                .Where(recurring => recurring.StoppedAt == null && recurring.PausedAt == null && recurring.NextOn <= today)
                .Select(recurring => recurring.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var id in due)
        {
            try
            {
                await CatchUp(id, today, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One that fails in a way nobody can fix from the app does not hold up the rest.
                logger.LogError(e, "Creating the occurrences of repeating expense {Id} failed.", id);
            }
        }
    }

    /// <summary>Every occurrence still owed, oldest first, until one cannot be created.</summary>
    private async Task CatchUp(Guid id, DateOnly today, CancellationToken ct)
    {
        while (true)
        {
            string problem;

            await using (var scope = scopes.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var context = services.GetRequiredService<AppDbContext>();
                var recurring = await context.Set<RecurringExpense>()
                    .Include(r => r.EnteredBy)
                    .FirstOrDefaultAsync(r => r.Id == id, ct);

                if (recurring is null || recurring.StoppedAt is not null || recurring.PausedAt is not null ||
                    recurring.NextOn > today)
                {
                    return;
                }

                services.GetRequiredService<ICurrentUserInitializer>().Initialize(recurring.EnteredBy);
                var template = JsonSerializer.Deserialize<RecurringExpenseRequest>(recurring.Template,
                    RecurringExpenseService.Json)!;

                var day = recurring.NextOn;
                recurring.NextOn = Recurrence.After(recurring.StartsOn, recurring.Frequency, day);
                recurring.Problem = null;

                try
                {
                    await services.GetRequiredService<ITransactionService>()
                        .Create(template.On(day), TransactionSource.Schedule, ct);
                    continue;
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Another run created this day first.
                    return;
                }
                catch (DomainException e)
                {
                    problem = e.Message;
                }
            }

            await Report(id, problem, ct);
            return;
        }
    }

    private async Task Report(Guid id, string problem, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recurring = await context.Set<RecurringExpense>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (recurring is null)
        {
            return;
        }

        recurring.Problem = problem.Length > ProblemLength ? problem[..ProblemLength] : problem;
        await context.SaveChangesAsync(ct);
        logger.LogInformation("Repeating expense {Id} could not create its occurrence on {Day}: {Problem}",
            id, recurring.NextOn, problem);
    }
}
