using System.Linq.Expressions;
using GroupSplit.API.Services.BackgroundJobs;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Push;

public sealed class PushNotificationQueue(AppDbContext db, IBackgroundJobQueue jobs, TimeProvider clock)
{
    internal static Expression<Func<PushDevice, bool>> Enabled(PushNotificationKind kind) => PushNotificationKinds.Category(kind) switch
    {
        PushNotificationCategory.Expenses => d => d.ExpensesEnabled,
        PushNotificationCategory.Settlements => d => d.SettlementsEnabled,
        PushNotificationCategory.GuestsJoining => d => d.InvitationsEnabled,
        PushNotificationCategory.BankSyncIssues => d => d.BankSyncIssuesEnabled,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public async Task ForGroup(PushNotificationKind kind, Guid groupId, Guid actorId, CancellationToken ct,
        Guid? subjectId = null, Guid[]? recipients = null)
    {
        var users = db.Set<User>().Where(u => u.Kind == UserKind.Account && u.Id != actorId
            && u.Groups.Any(g => g.Id == groupId));
        if (recipients is not null) users = users.Where(u => recipients.Contains(u.Id));
        await Enqueue(kind, groupId, subjectId, users.Select(u => u.Id), ct);
    }

    public Task ForBankIssue(Guid ownerId, Guid connectionId, CancellationToken ct) =>
        Enqueue(PushNotificationKind.BankSyncIssue, Guid.Empty, connectionId,
            db.Set<User>().Where(u => u.Id == ownerId && u.Kind == UserKind.Account).Select(u => u.Id), ct);

    private async Task Enqueue(PushNotificationKind kind, Guid groupId, Guid? subjectId,
        IQueryable<Guid> users, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var devices = await db.Set<PushDevice>().Where(Enabled(kind))
            .Where(d => d.UpdatedAt > now.AddDays(-30) && users.Contains(d.UserId))
            .Select(d => new { d.Id, d.UserId }).ToListAsync(ct);
        foreach (var device in devices)
            jobs.Enqueue(new SendPushNotification(device.Id, device.UserId, groupId, Guid.NewGuid(),
                now.AddHours(1), kind, subjectId));
    }
}
