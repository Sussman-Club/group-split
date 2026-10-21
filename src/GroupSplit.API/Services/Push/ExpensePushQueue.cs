using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Push;

public static class ExpensePushQueue
{
    public static async Task Enqueue(AppDbContext db, Guid groupId, Guid actorId, CancellationToken ct)
    {
        var devices = await db.Set<PushDevice>()
            .Where(d => d.UserId != actorId && d.ExpensesEnabled && d.UpdatedAt > DateTimeOffset.UtcNow.AddDays(-30)
                && db.Set<User>().Any(u => u.Id == d.UserId && u.Kind == UserKind.Account
                    && u.Groups.Any(g => g.Id == groupId)))
            .Select(d => new { d.Id, d.UserId }).ToListAsync(ct);
        foreach (var device in devices)
            db.Add(new PushDelivery { DeviceId = device.Id, UserId = device.UserId, GroupId = groupId,
                NextAttemptAt = DateTimeOffset.UtcNow });
    }
}
