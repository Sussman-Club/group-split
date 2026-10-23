using GroupSplit.API.Extensions;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Endpoints;

public static class PushApi
{
    public static void MapPushApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/users/me/push-devices").RequireAuthorization()
            .WithTags("Push notifications").ProducesStandardProblems();
        group.MapPut("/{id:guid}", async Task<IResult> (Guid id, PushDeviceRequest request,
            ICurrentUser current, AppDbContext db, CancellationToken ct) =>
        {
            // Keep the unique PostgreSQL index below its per-entry byte limit, including UTF-8 input.
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(request.Token)
                || System.Text.Encoding.UTF8.GetByteCount(request.Token) > 2048)
                return Results.BadRequest();
            var device = await db.Set<PushDevice>().FindAsync([id], ct);
            // An installation id is scoped to its owner. Sign-out unregisters it before a new login.
            if (device is not null && device.UserId != current.User.Id) return Results.NotFound();
            var duplicate = await db.Set<PushDevice>().SingleOrDefaultAsync(d => d.Token == request.Token && d.Id != id, ct);
            if (duplicate is not null)
            {
                if (duplicate.UserId != current.User.Id) return Results.Conflict();
                db.Remove(duplicate);
                await db.SaveChangesAsync(ct);
            }
            if (device is null)
            {
                device = new PushDevice { Id = id, UserId = current.User.Id };
                db.Add(device);
            }
            device.Token = request.Token;
            device.ExpensesEnabled = request.ExpensesEnabled;
            device.ExpenseChangesEnabled = request.ExpenseChangesEnabled;
            device.SettlementsEnabled = request.SettlementsEnabled;
            device.PaymentRequestsEnabled = request.PaymentRequestsEnabled;
            device.InvitationsEnabled = request.InvitationsEnabled;
            device.BankSyncIssuesEnabled = request.BankSyncIssuesEnabled;
            device.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("RegisterPushDevice");
        group.MapDelete("/{id:guid}", async (Guid id, ICurrentUser current, AppDbContext db, CancellationToken ct) =>
        {
            var device = await db.Set<PushDevice>().SingleOrDefaultAsync(d => d.Id == id && d.UserId == current.User.Id, ct);
            if (device is not null) { db.Remove(device); await db.SaveChangesAsync(ct); }
            return Results.NoContent();
        }).WithName("UnregisterPushDevice");
    }
}
