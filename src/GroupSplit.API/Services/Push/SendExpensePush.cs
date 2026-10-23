using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Push;

// Only identifiers and expiry cross the durable boundary; tokens are read at delivery time.
public sealed record SendExpensePush(Guid DeviceId, Guid UserId, Guid GroupId,
    Guid DeliveryId, DateTimeOffset ExpiresAt) : IJob
{
    public const string Name = "push.send-expense";
}

public sealed class SendExpensePushHandler(AppDbContext db, IPushSender sender, TimeProvider clock)
    : IJobHandler<SendExpensePush>
{
    public async ValueTask HandleAsync(SendExpensePush job, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (job.ExpiresAt <= now) return;
        var device = await db.Set<PushDevice>().SingleOrDefaultAsync(d => d.Id == job.DeviceId
            && d.UserId == job.UserId && d.ExpensesEnabled && d.UpdatedAt > now.AddDays(-30), ct);
        if (device is null) return;
        var member = await db.Set<User>().AnyAsync(u => u.Id == job.UserId && u.Kind == UserKind.Account
            && u.Groups.Any(g => g.Id == job.GroupId), ct);
        if (!member) return;

        // Provider failures escape to the shared queue's retry policy. Never persist tokens or errors.
        if (!await sender.SendAsync(device.Token, job.GroupId, job.DeliveryId, ct))
        {
            db.Remove(device);
            await db.SaveChangesAsync(ct);
        }
    }
}
