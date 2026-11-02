using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>Support context only: no credentials, balances, bank details, or expense contents.</summary>
public sealed class SupportUserService(AppDbContext db)
{
    public async Task<List<SupportUserResponse>> SearchAsync(string? search, CancellationToken ct)
    {
        var needle = search?.Trim().ToLowerInvariant() ?? "";
        if (needle.Length < 2 || needle.Length > 200)
            throw new ValidationException(ErrorCodes.BadRequest, "Search for at least two characters, up to two hundred.");
        return await Describe(db.Set<User>().Where(u => u.Kind == UserKind.Account
            && ((u.Email != null && u.Email.ToLower().Contains(needle))
                || ((u.FirstName ?? "") + " " + (u.LastName ?? "")).ToLower().Contains(needle))).OrderBy(u => u.FirstName).ThenBy(u => u.LastName).ThenBy(u => u.Id).Take(20)).ToListAsync(ct);
    }

    public async Task<DirectoryPage<SupportUserResponse>> ListAsync(string? search, int page, CancellationToken ct)
    {
        var needle = search?.Trim().ToLowerInvariant() ?? "";
        if (needle.Length > 200 || page is < 0 or > 10000)
            throw new ValidationException(ErrorCodes.BadRequest, "Check the search and page number.");
        var query = db.Set<User>().AsNoTracking().Where(u => u.Kind == UserKind.Account);
        if (needle.Length > 0)
            query = query.Where(u => (u.Email != null && u.Email.ToLower().Contains(needle))
                || ((u.FirstName ?? "") + " " + (u.LastName ?? "")).ToLower().Contains(needle));
        var rows = await Describe(query.OrderBy(u => u.FirstName).ThenBy(u => u.LastName).ThenBy(u => u.Id)
            .Skip(page * 30).Take(31)).ToListAsync(ct);
        return new(rows.Take(30).ToList(), rows.Count > 30);
    }

    public async Task<SupportUserResponse> GetAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        var user = await Describe(db.Set<User>().Where(u => u.Id == id && u.Kind == UserKind.Account)).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException(ErrorCodes.UserNotFound, "Account not found.");
        return user;
    }

    private IQueryable<SupportUserResponse> Describe(IQueryable<User> query) => query.Select(u =>
        new SupportUserResponse(u.Id, ((u.FirstName ?? "") + " " + (u.LastName ?? "")).Trim(), u.Email,
            "Active account", u.Groups.Count, db.Set<SupportTicket>().Count(t => t.ReporterUserId == u.Id
                && t.Status != SupportTicketStatus.Resolved && t.Status != SupportTicketStatus.Closed)));
}
