using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Mvc;

namespace GroupSplit.API.Endpoints;

public static class StaffApi
{
    public static void MapStaffApi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/staff/access", async (System.Security.Claims.ClaimsPrincipal user, ICurrentUser current, StaffTeamService team, CancellationToken ct) =>
        {
            var allowed = StaffAccess.IsStaff(user);
            await team.RecordAccessAsync(current.User.Id, allowed, ct);
            return new StaffAccessResponse(allowed);
        }).RequireAuthorization().ProducesStandardProblems().WithTags("Staff");
        var group = routes.MapGroup("/staff")
            .RequireAuthorization(policy => policy.RequireAuthenticatedUser()
                .RequireAssertion(context => StaffAccess.IsStaff(context.User)))
            .ProducesStandardProblems()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithTags("Staff");

        group.AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            await services.GetRequiredService<StaffTeamService>().RecordAccessAsync(
                services.GetRequiredService<ICurrentUser>().User.Id, true, context.HttpContext.RequestAborted);
            return await next(context);
        });
        group.MapStaffOperations();
        group.MapGet("/merchants", (string? search, int? page, string? sort, IMerchantService merchants, CancellationToken ct) =>
            merchants.ListDirectory(search, page, sort, ct));
        group.MapPost("/merchants", (CreateMerchantRequest request, IMerchantService merchants, ICurrentUser current, CancellationToken ct) =>
            merchants.CreateDirectory(request, current.User.Id, ct));
        group.MapGet("/merchants/{id:guid}", (Guid id, IMerchantService merchants, CancellationToken ct) =>
            merchants.GetDirectory(id, ct));
        group.MapPost("/merchant-images", ([FromForm] Guid? merchantId, [FromForm] string name,
            [FromForm] Guid? expectedVersion, IFormFile file, MerchantImageService images, ICurrentUser current, CancellationToken ct) =>
            images.SaveAsync(merchantId, name, file, current.User.Id, expectedVersion, ct))
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MerchantLogos.MaximumUploadBytes + 65536));
        group.MapPut("/merchants/{id:guid}", async Task<IResult> (Guid id, UpdateMerchantRequest request, IMerchantService merchants, ICurrentUser current, CancellationToken ct) =>
        {
            await merchants.UpdateDirectory(id, request, current.User.Id, ct);
            return Results.NoContent();
        });
        group.MapGet("/tickets", (string? search, string? status, string? priority, bool? needsReview, int? page,
            string? assignee, string? sort, string? type, SupportTicketService tickets, ICurrentUser current, CancellationToken ct) =>
        {
            Guid? assignedId = null;
            if (assignee == "me")
            {
                assignedId = current.User.Id;
            }
            else if (!string.IsNullOrEmpty(assignee) && assignee != "unassigned")
            {
                if (!Guid.TryParse(assignee, out var parsed))
                {
                    throw new ValidationException(GroupSplit.Shared.Errors.ErrorCodes.BadRequest, "Choose a valid assignee.");
                }

                assignedId = parsed;
            }
            return tickets.ListAsync(new SupportTicketQuery
            {
                Search = search,
                Status = status,
                Priority = priority,
                Type = type,
                NeedsReview = needsReview == true,
                Page = page ?? 0,
                AssignedUserId = assignedId,
                UnassignedOnly = assignee == "unassigned",
                Sort = sort ?? "updated"
            }, ct);
        });
        group.MapPut("/tickets/{id:guid}", (Guid id, UpdateSupportTicketRequest request, SupportTicketService tickets, ICurrentUser current, CancellationToken ct) =>
            tickets.UpdateAsync(id, request, current.User.Id, ct));
        group.MapGet("/tickets/{id:guid}", (Guid id, SupportTicketService tickets, CancellationToken ct) =>
            tickets.GetAsync(id, ct));
        group.MapGet("/tickets/{id:guid}/notes", (Guid id, SupportTicketService tickets, CancellationToken ct) =>
            tickets.ListNotesAsync(id, ct));
        group.MapPost("/tickets/{id:guid}/notes", (Guid id, CreateSupportNoteRequest request, SupportTicketService tickets, ICurrentUser current, CancellationToken ct) =>
            tickets.AddNoteAsync(id, request, current.User.Id, ct));
    }
}
