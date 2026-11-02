using GroupSplit.API.Services;
using GroupSplit.Shared;

namespace GroupSplit.API.Endpoints;

public static class StaffOperationsApi
{
    public static void MapStaffOperations(this RouteGroupBuilder group)
    {
        group.MapGet("/team", (StaffTeamService team, CancellationToken ct) => team.ListAsync(ct));
        group.MapGet("/activity", (string? resource, Guid? resourceId, int? page, StaffActivityService activity, CancellationToken ct) =>
            activity.ListAsync(resource, resourceId, page ?? 0, ct));
        group.MapGet("/users", (string? search, SupportUserService users, CancellationToken ct) => users.SearchAsync(search, ct));
        group.MapGet("/users/directory", (string? search, int? page, SupportUserService users, CancellationToken ct) => users.ListAsync(search, page ?? 0, ct));
        group.MapGet("/users/{id:guid}", (Guid id, SupportUserService users, ICurrentUser current, CancellationToken ct) => users.GetAsync(id, current.User.Id, ct));
        group.MapGet("/views", (string resource, StaffSavedViewService views, ICurrentUser current, CancellationToken ct) => views.ListAsync(current.User.Id, resource, ct));
        group.MapPost("/views", (SaveStaffViewRequest request, StaffSavedViewService views, ICurrentUser current, CancellationToken ct) => views.SaveAsync(current.User.Id, request, ct));
        group.MapDelete("/views/{id:guid}", async (Guid id, StaffSavedViewService views, ICurrentUser current, CancellationToken ct) =>
        { await views.DeleteAsync(current.User.Id, id, ct); return Results.NoContent(); });
        group.MapPost("/tickets/bulk", async (BulkTicketUpdateRequest request, SupportTicketService tickets, ICurrentUser current, CancellationToken ct) =>
        { await tickets.BulkUpdateAsync(request, current.User.Id, ct); return Results.NoContent(); });
        group.MapGet("/tickets/{id:guid}/messages", (Guid id, int? page, SupportTicketService tickets, CancellationToken ct) => tickets.StaffMessagesAsync(id, page ?? 0, ct));
        group.MapPost("/tickets/{id:guid}/reply", (Guid id, CreateSupportMessageRequest request, SupportTicketService tickets, ICurrentUser current, CancellationToken ct) =>
            tickets.StaffReplyAsync(id, request, current.User.Id, ct));
        group.MapGet("/merchants/{id:guid}/aliases", (Guid id, IMerchantService merchants, CancellationToken ct) => merchants.Aliases(id, ct));
        group.MapPost("/merchants/{id:guid}/aliases", async (Guid id, CreateMerchantAliasRequest request, IMerchantService merchants, ICurrentUser current, CancellationToken ct) =>
        { await merchants.AddAlias(id, request.Name, current.User.Id, ct); return Results.NoContent(); });
        group.MapDelete("/merchants/{id:guid}/aliases/{aliasId:guid}", async (Guid id, Guid aliasId, IMerchantService merchants, ICurrentUser current, CancellationToken ct) =>
        { await merchants.RemoveAlias(id, aliasId, current.User.Id, ct); return Results.NoContent(); });
        group.MapGet("/merchants/{id:guid}/merge-preview", (Guid id, Guid targetId, IMerchantService merchants, CancellationToken ct) => merchants.PreviewMerge(id, targetId, ct));
        group.MapPost("/merchants/{id:guid}/merge", (Guid id, MergeMerchantsRequest request, IMerchantService merchants, ICurrentUser current, CancellationToken ct) => merchants.Merge(id, request, current.User.Id, ct));
    }
}
