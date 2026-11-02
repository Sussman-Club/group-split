using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>Team presence is recorded by the authorization boundary; it never grants access.</summary>
public sealed class StaffTeamService(AppDbContext db, TimeProvider clock)
{
    public async Task RecordAccessAsync(Guid userId, bool active, CancellationToken ct)
    {
        var member = await db.Set<StaffMember>().SingleOrDefaultAsync(m => m.UserId == userId, ct);
        if (member is null && !active) return;
        var now = clock.GetUtcNow();
        if (member is not null && member.Active == active && member.LastVerifiedAt > now.AddHours(-1)) return;
        if (member is null) { member = new StaffMember { UserId = userId }; db.Add(member); }
        member.Active = active; member.LastVerifiedAt = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        { db.Entry(member).State = EntityState.Detached; }
    }

    public async Task<List<StaffPersonResponse>> ListAsync(CancellationToken ct) =>
        await (from m in db.Set<StaffMember>().AsNoTracking()
            join u in db.Set<User>() on m.UserId equals u.Id
            where m.Active && u.Kind == UserKind.Account
            orderby u.FirstName, u.LastName, u.Id
            select new StaffPersonResponse(u.Id, ((u.FirstName ?? "") + " " + (u.LastName ?? "")).Trim(), u.Email)).ToListAsync(ct);

    public async Task ValidateAssigneeAsync(Guid? userId, CancellationToken ct)
    {
        if (userId is null) return;
        if (!await (from m in db.Set<StaffMember>() join u in db.Set<User>() on m.UserId equals u.Id
            where m.UserId == userId && m.Active && u.Kind == UserKind.Account select m.Id).AnyAsync(ct))
            throw new ValidationException(ErrorCodes.InvalidSupportTicket, "Choose an active staff member or leave the ticket unassigned.");
    }
}
