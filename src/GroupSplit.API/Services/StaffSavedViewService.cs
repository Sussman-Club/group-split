using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public sealed class StaffSavedViewService(AppDbContext db, TimeProvider clock)
{
    public Task<List<SavedStaffViewResponse>> ListAsync(Guid userId, string resource, CancellationToken ct) =>
        db.Set<StaffSavedView>().AsNoTracking().Where(v => v.UserId == userId && v.Resource == resource)
            .OrderBy(v => v.Name).Select(v => new SavedStaffViewResponse(v.Id, v.Name, v.Resource, v.Url)).ToListAsync(ct);

    public async Task<SavedStaffViewResponse> SaveAsync(Guid userId, SaveStaffViewRequest request, CancellationToken ct)
    {
        var path = request.Resource switch { "Tickets" => "/staff/tickets", "Merchants" => "/staff/merchants", _ => "" };
        if (path == "" || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 60
            || request.Url is null || request.Url.Length > 2048 || request.Url.Contains('#')
            || !(request.Url == path || request.Url.StartsWith(path + "?", StringComparison.Ordinal)))
            throw new ValidationException(ErrorCodes.BadRequest, "Choose a name and a valid staff list URL.");
        var name = request.Name.Trim();
        if (await db.Set<StaffSavedView>().CountAsync(v => v.UserId == userId && v.Resource == request.Resource, ct) >= 20)
            throw new ConflictException(ErrorCodes.Conflict, "You can save up to twenty views per section.");
        if (await db.Set<StaffSavedView>().AnyAsync(v => v.UserId == userId && v.Resource == request.Resource && v.Name == name, ct))
            throw new ConflictException(ErrorCodes.Conflict, "A saved view with this name already exists.");
        var view = new StaffSavedView { UserId = userId, Resource = request.Resource, Name = name, Url = request.Url, CreatedAt = clock.GetUtcNow() };
        db.Add(view);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        { throw new ConflictException(ErrorCodes.Conflict, "A saved view with this name already exists."); }
        return new(view.Id, view.Name, view.Resource, view.Url);
    }

    public async Task DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var view = await db.Set<StaffSavedView>().SingleOrDefaultAsync(v => v.Id == id && v.UserId == userId, ct)
            ?? throw new NotFoundException(ErrorCodes.NotFound, "Saved view not found.");
        db.Remove(view); await db.SaveChangesAsync(ct);
    }
}
