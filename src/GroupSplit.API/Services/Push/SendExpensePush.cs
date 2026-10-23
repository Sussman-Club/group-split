using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Push;

// Only identifiers and expiry cross the durable boundary; tokens are read at delivery time.
public sealed record SendExpensePush(Guid DeviceId, Guid UserId, Guid GroupId,
    Guid DeliveryId, DateTimeOffset ExpiresAt) : IJob
{
    public const string Name = "push.send-expense";
}

public sealed class SendExpensePushHandler(SendPushNotificationHandler delivery) : IJobHandler<SendExpensePush>
{
    public ValueTask HandleAsync(SendExpensePush job, CancellationToken ct) => delivery.HandleAsync(
        new SendPushNotification(job.DeviceId, job.UserId, job.GroupId, job.DeliveryId, job.ExpiresAt,
            PushNotificationKind.ExpenseCreated), ct);
}

public sealed record SendPushNotification(Guid DeviceId, Guid UserId, Guid GroupId, Guid DeliveryId,
    DateTimeOffset ExpiresAt, PushNotificationKind Kind, Guid? SubjectId = null) : IJob
{
    public const string Name = "push.send-notification";
}

public sealed class SendPushNotificationHandler(AppDbContext db, IPushSender sender, TimeProvider clock)
    : IJobHandler<SendPushNotification>
{
    public async ValueTask HandleAsync(SendPushNotification job, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (job.ExpiresAt <= now || !Enum.IsDefined(job.Kind)) return;
        var device = await db.Set<PushDevice>().Where(PushNotificationQueue.Enabled(job.Kind))
            .SingleOrDefaultAsync(d => d.Id == job.DeviceId && d.UserId == job.UserId
                && d.UpdatedAt > now.AddDays(-30), ct);
        if (device is null) return;
        string? groupName = null;
        string? requestToken = null;
        if (job.Kind == PushNotificationKind.BankSyncIssue)
        {
            var relevant = await db.Set<BankConnection>().AnyAsync(b => b.Id == job.SubjectId
                && b.UserId == job.UserId && b.User.Kind == UserKind.Account
                && (b.Status != BankConnectionStatus.Active || b.SignInExpiring || b.AccountsNotShared
                    || b.Accounts.Any(a => a.AccessRevoked)), ct);
            if (!relevant) return;
        }
        else
        {
            groupName = await db.Set<Group>()
                .Where(g => g.Id == job.GroupId && g.Users.Any(u => u.Id == job.UserId && u.Kind == UserKind.Account))
                .Select(g => g.Name).SingleOrDefaultAsync(ct);
            if (groupName is null) return;
            if (job.Kind == PushNotificationKind.PaymentRequest)
            {
                requestToken = await db.Set<SettlementRequest>()
                    .Where(r => r.Id == job.SubjectId && r.RecipientUserId == job.UserId
                        && r.CancelledAt == null && r.ResolvedAt == null && r.StaleAt == null
                        && r.Groups.Any(g => g.GroupId == job.GroupId))
                    .Select(r => r.Token).SingleOrDefaultAsync(ct);
                if (requestToken is null) return;
            }
        }
        if (!await sender.SendAsync(device.Token, job.GroupId, groupName ?? "", job.DeliveryId, ct,
            job.Kind, requestToken))
        {
            db.Remove(device);
            await db.SaveChangesAsync(ct);
        }
    }
}
