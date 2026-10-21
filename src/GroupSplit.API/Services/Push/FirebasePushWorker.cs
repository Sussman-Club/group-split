using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Push;

public sealed class FirebasePushWorker(IServiceScopeFactory scopes, IPushSender sender,
    ILogger<FirebasePushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Deliver(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Push delivery batch failed; it will be retried."); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    internal async Task Deliver(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var deliveries = await db.Set<PushDelivery>().Where(d => d.NextAttemptAt <= DateTimeOffset.UtcNow)
            .OrderBy(d => d.NextAttemptAt).Take(50).ToListAsync(ct);
        foreach (var delivery in deliveries)
        {
            if (db.Entry(delivery).State == EntityState.Detached) continue;
            // Optimistic lease prevents two API instances from normally sending the same row.
            delivery.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(5);
            delivery.Attempts++;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { db.Entry(delivery).State = EntityState.Detached; continue; }
            var device = await db.Set<PushDevice>().SingleOrDefaultAsync(d => d.Id == delivery.DeviceId
                && d.UserId == delivery.UserId && d.ExpensesEnabled
                && d.UpdatedAt > DateTimeOffset.UtcNow.AddDays(-30), ct);
            var member = await db.Set<User>().AnyAsync(u => u.Id == delivery.UserId && u.Kind == UserKind.Account
                && u.Groups.Any(g => g.Id == delivery.GroupId), ct);
            if (device is null || !member || delivery.Attempts > 5 || delivery.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                db.Remove(delivery);
                await db.SaveChangesAsync(ct);
                continue;
            }
            try
            {
                var registered = await sender.SendAsync(device.Token, delivery.GroupId, delivery.Id, ct);
                if (!registered) db.Remove(device);
                db.Remove(delivery);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Never log tokens, payloads, or provider errors which may contain credentials.
                logger.LogWarning("Push delivery failed on attempt {Attempt}.", delivery.Attempts);
                if (delivery.Attempts >= 5) db.Remove(delivery);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
