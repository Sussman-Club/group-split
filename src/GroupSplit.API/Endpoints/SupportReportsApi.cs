using GroupSplit.API.Services;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Endpoints;

public static class SupportReportsApi
{
    public static void MapSupportReportsApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/support-reports").RequireAuthorization().WithTags("Support reports");
        group.MapGet("/", async (ICurrentUser current, AppDbContext db, CancellationToken ct) =>
        {
            var tickets = await db.Set<SupportTicket>().AsNoTracking()
                .Where(t => t.ReporterUserId == current.User.Id)
                .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
                .Take(100).ToListAsync(ct);
            return tickets.Select(t => new SupportReportResponse(t.Id, t.Type.ToString(), t.Status.ToString(),
                t.Title, t.Description, t.ReproductionSteps, t.CreatedAt)).ToList();
        }).WithName("ListSupportReports");

        group.MapPost("/", async Task<IResult> (CreateSupportReportRequest request, ICurrentUser current,
            AppDbContext db, CancellationToken ct) =>
        {
            if (!Enum.TryParse<SupportTicketType>(request.Type, false, out var type)
                || !Enum.IsDefined(type) || string.IsNullOrWhiteSpace(request.Title)
                || request.Title.Length > 200 || string.IsNullOrWhiteSpace(request.Description)
                || request.Description.Length > 10000 || request.ReproductionSteps?.Length > 10000)
                return Results.BadRequest();
            var now = DateTimeOffset.UtcNow;
            var ticket = new SupportTicket { ReporterUserId = current.User.Id, Type = type,
                Title = request.Title.Trim(), Description = request.Description.Trim(),
                ReproductionSteps = string.IsNullOrWhiteSpace(request.ReproductionSteps) ? null : request.ReproductionSteps.Trim(),
                CreatedAt = now, UpdatedAt = now };
            db.Add(ticket);
            db.Add(new SupportTicketStatusChange { TicketId = ticket.Id, ChangedByUserId = current.User.Id,
                ToStatus = SupportTicketStatus.New, ChangedAt = now });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new SupportReportResponse(ticket.Id, ticket.Type.ToString(), ticket.Status.ToString(),
                ticket.Title, ticket.Description, ticket.ReproductionSteps, ticket.CreatedAt));
        }).WithName("CreateSupportReport");
    }
}
