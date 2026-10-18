using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>Stages request resolution in the same save as the corresponding ledger transfers.</summary>
internal static class SettlementRequestResolution
{
    public static async Task ResolveMatching(AppDbContext context, TimeProvider clock, Guid fromUserId,
        Guid toUserId, IReadOnlyList<GroupDebt> payments, CancellationToken cancellationToken = default)
    {
        var candidates = await context.Set<SettlementRequest>().Include(request => request.Groups)
            .Where(request => request.RecipientUserId == fromUserId && request.RequesterUserId == toUserId &&
                              request.CancelledAt == null && request.ResolvedAt == null && request.StaleAt == null)
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var request in candidates.Where(request => request.Groups.Count == payments.Count &&
            request.Groups.All(part => payments.Any(payment => payment.GroupId == part.GroupId &&
                payment.Amount == part.RequestedAmount))))
            request.ResolvedAt = now;
    }
}
