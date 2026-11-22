using GroupSplit.API.Errors;
using GroupSplit.API.Services.RecurringExpenses;
using GroupSplit.Shared;

namespace GroupSplit.API.Endpoints;

/// <summary>
/// Expenses that repeat on a schedule (#546). See <see cref="IRecurringExpenseService"/>.
/// </summary>
public static class RecurringExpensesApi
{
    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapRecurringExpensesApi()
        {
            var group = routes.MapGroup("/recurring-expenses")
                .RequireAuthorization()
                .ProducesStandardProblems();

            group.WithTags("Recurring expenses");

            group.MapListRecurringExpenses();
            group.MapCreateRecurringExpense();
            group.MapRepeatExpense();
            group.MapUpdateRecurringExpense();
            group.MapPauseRecurringExpense();
            group.MapResumeRecurringExpense();
            group.MapStopRecurringExpense();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapListRecurringExpenses()
        {
            return group.MapGet(string.Empty, async (
                    Guid groupId,
                    IRecurringExpenseService recurring,
                    CancellationToken ct) => Results.Ok(await recurring.InGroup(groupId, ct)))
                .WithName("GetRecurringExpenses")
                .Produces<List<RecurringExpenseResponse>>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapCreateRecurringExpense()
        {
            return group.MapPost(string.Empty, async (
                    RecurringExpenseRequest request,
                    IRecurringExpenseService recurring,
                    CancellationToken ct) => Results.Ok(await recurring.Create(request, ct)))
                .WithName("CreateRecurringExpense")
                .Produces<RecurringExpenseResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapRepeatExpense()
        {
            return group.MapPost("/from-expense/{expenseId:guid}", async (
                    Guid expenseId,
                    RepeatExpenseRequest request,
                    IRecurringExpenseService recurring,
                    CancellationToken ct) => Results.Ok(await recurring.Repeat(expenseId, request, ct)))
                .WithName("RepeatExpense")
                .Produces<RecurringExpenseResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapUpdateRecurringExpense()
        {
            return group.MapPut("/{id:guid}", async (
                    Guid id,
                    RecurringExpenseRequest request,
                    IRecurringExpenseService recurring,
                    CancellationToken ct) => Results.Ok(await recurring.Update(id, request, ct)))
                .WithName("UpdateRecurringExpense")
                .Produces<RecurringExpenseResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapPauseRecurringExpense()
        {
            return group.MapPost("/{id:guid}/pause", async (
                    Guid id,
                    IRecurringExpenseService recurring,
                    CancellationToken ct) => Results.Ok(await recurring.Pause(id, ct)))
                .WithName("PauseRecurringExpense")
                .Produces<RecurringExpenseResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapResumeRecurringExpense()
        {
            return group.MapPost("/{id:guid}/resume", async (
                    Guid id,
                    IRecurringExpenseService recurring,
                    CancellationToken ct) => Results.Ok(await recurring.Resume(id, ct)))
                .WithName("ResumeRecurringExpense")
                .Produces<RecurringExpenseResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapStopRecurringExpense()
        {
            return group.MapDelete("/{id:guid}", async (
                    Guid id,
                    IRecurringExpenseService recurring,
                    CancellationToken ct) =>
                {
                    await recurring.Stop(id, ct);
                    return Results.NoContent();
                })
                .WithName("StopRecurringExpense")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
