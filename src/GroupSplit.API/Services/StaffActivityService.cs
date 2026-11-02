using System.Text.Json;
using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public sealed class StaffActivityService(AppDbContext db)
{
    public static void Record(AppDbContext db, Guid? actor, string resource, Guid id, string action,
        DateTimeOffset at, params ActivityChange[] changes) => db.Add(new StaffActivity
    {
        ActorUserId = actor, Resource = resource, ResourceId = id, Action = action,
        At = at, ChangesJson = JsonSerializer.Serialize(changes)
    });

    public async Task<DirectoryPage<StaffActivityResponse>> ListAsync(string? resource, Guid? resourceId, int page, CancellationToken ct)
    {
        if (page is < 0 or > 10000 || resource is not (null or "" or "Tickets" or "Merchants" or "Users"))
            throw new ValidationException(ErrorCodes.BadRequest, "Choose a valid activity filter.");
        var query = db.Set<StaffActivity>().AsNoTracking().Where(a => a.Action != "Account viewed");
        if (!string.IsNullOrEmpty(resource)) query = query.Where(a => a.Resource == resource);
        if (resourceId is not null) query = query.Where(a => a.ResourceId == resourceId);
        var rows = await query.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Skip(page * 30).Take(31).ToListAsync(ct);
        var ids = rows.Where(a => a.ActorUserId is not null).Select(a => a.ActorUserId!.Value).ToArray();
        var people = await db.Set<User>().AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Kind }).ToDictionaryAsync(u => u.Id, ct);
        var result = rows.Take(30).Select(a => new StaffActivityResponse(a.Id, a.Resource, a.ResourceId, a.Action,
            a.ActorUserId is { } actor && people.TryGetValue(actor, out var p) && p.Kind == UserKind.Account
                ? ((p.FirstName ?? "") + " " + (p.LastName ?? "")).Trim() : "System or deleted account",
            a.At, JsonSerializer.Deserialize<List<ActivityChange>>(a.ChangesJson) ?? [])).ToList();
        return new(result, rows.Count > 30);
    }
}
