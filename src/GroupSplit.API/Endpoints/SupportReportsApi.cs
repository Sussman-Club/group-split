using GroupSplit.API.Services;
using GroupSplit.API.Errors;
using GroupSplit.Shared;

namespace GroupSplit.API.Endpoints;

public static class SupportReportsApi
{
    public static void MapSupportReportsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/support-reports").RequireAuthorization().ProducesStandardProblems()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithTags("Support reports");
        group.MapGet("/notifications", (ICurrentUser current, SupportTicketService tickets, CancellationToken ct) => tickets.UnreadCountAsync(current.User.Id, ct));
        group.MapGet("/{id:guid}/messages", (Guid id, int? page, ICurrentUser current, SupportTicketService tickets, CancellationToken ct) => tickets.ReporterMessagesAsync(id, current.User.Id, page ?? 0, ct));
        group.MapPost("/{id:guid}/messages", (Guid id, CreateSupportMessageRequest request, ICurrentUser current, SupportTicketService tickets, CancellationToken ct) => tickets.ReporterReplyAsync(id, request, current.User.Id, ct));
        group.MapPost("/{id:guid}/read", async (Guid id, ICurrentUser current, SupportTicketService tickets, CancellationToken ct) =>
        { await tickets.MarkReadAsync(id, current.User.Id, ct); return Results.NoContent(); });
        group.MapGet("/", (ICurrentUser current, SupportTicketService tickets, CancellationToken ct) =>
            tickets.ListForReporterAsync(current.User.Id, ct)).WithName("ListSupportReports");
        group.MapPost("/", (CreateSupportReportRequest request, ICurrentUser current, SupportTicketService tickets, CancellationToken ct) =>
            tickets.CreateAsync(request, current.User.Id, ct)).WithName("CreateSupportReport");
    }
}
