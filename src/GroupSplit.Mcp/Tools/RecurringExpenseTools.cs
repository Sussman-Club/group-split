using System.ComponentModel;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class RecurringExpenseTools
{
    [McpServerTool(Name = "list_recurring_expenses", ReadOnly = true, Destructive = false, Idempotent = true), Description("List a group's repeating expenses that have not been stopped, the next due first. Each says its next day, whether it is paused, and why its next day could not be created when something is in the way (problem).")]
    public static Task<ICollection<RecurringExpenseResponse>> ListRecurringExpenses(Recurring_expensesClient client, Guid groupId, CancellationToken ct = default) =>
        client.GetRecurringExpensesAsync(groupId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "create_recurring_expense", ReadOnly = false, Destructive = false, Idempotent = false), Description("Set up an expense that repeats weekly, monthly or yearly from startsOn. Each occurrence becomes an ordinary expense on its day, with source Schedule. A startsOn in the past only fixes the day; nothing before today is created. Fields mean what they mean on create_expense. To record today's and repeat it, pass repeat on create_expense instead. Confirm the amount, payer and split with the user first.")]
    public static Task<RecurringExpenseResponse> CreateRecurringExpense(Recurring_expensesClient client, RecurringExpenseRequest recurring, CancellationToken ct = default) =>
        client.CreateRecurringExpenseAsync(recurring, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "repeat_expense", ReadOnly = false, Destructive = false, Idempotent = false), Description("Make an existing expense repeat: the same group, payer, category, split and amount, from the occurrence after it. Pass from as the expense's day in the user's time zone when it was entered near midnight.")]
    public static Task<RecurringExpenseResponse> RepeatExpense(Recurring_expensesClient client, Guid expenseId, RepeatFrequency frequency, DateOnly? from = null, CancellationToken ct = default) =>
        client.RepeatExpenseAsync(expenseId, new RepeatExpenseRequest(frequency, from), ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "update_recurring_expense", ReadOnly = false, Destructive = false, Idempotent = true), Description("Change a repeating expense. Send its complete details (start from list_recurring_expenses' details). Only occurrences still to come change; created expenses are edited as expenses. The group cannot change.")]
    public static Task<RecurringExpenseResponse> UpdateRecurringExpense(Recurring_expensesClient client, Guid recurringExpenseId, RecurringExpenseRequest recurring, CancellationToken ct = default) =>
        client.UpdateRecurringExpenseAsync(recurringExpenseId, recurring, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "set_recurring_expense_paused", ReadOnly = false, Destructive = false, Idempotent = true), Description("Pause or resume a repeating expense. Days that pass while it is paused are not created later.")]
    public static Task<RecurringExpenseResponse> SetRecurringExpensePaused(Recurring_expensesClient client, Guid recurringExpenseId, bool paused, CancellationToken ct = default) =>
        paused ? client.PauseRecurringExpenseAsync(recurringExpenseId, ct) : client.ResumeRecurringExpenseAsync(recurringExpenseId, ct);

    [Authorize(Policy = "mcp:write")]
    [McpServerTool(Name = "stop_recurring_expense", ReadOnly = false, Destructive = true, Idempotent = true), Description("Stop a repeating expense for good, after the user confirms. Expenses it already created stay.")]
    public static async Task StopRecurringExpense(Recurring_expensesClient client, Guid recurringExpenseId, CancellationToken ct = default) =>
        await client.StopRecurringExpenseAsync(recurringExpenseId, ct);
}
