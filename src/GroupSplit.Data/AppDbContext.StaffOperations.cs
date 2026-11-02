using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Data;

public partial class AppDbContext
{
    private static void ConfigureStaffOperations(ModelBuilder model)
    {
        model.Entity<Merchant>().Property(m => m.Version).IsConcurrencyToken();
        model.Entity<MerchantAlias>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(128);
            e.Property(a => a.NormalizedName).HasMaxLength(128);
            e.HasIndex(a => a.NormalizedName).IsUnique();
            e.HasIndex(a => a.MerchantId);
            e.HasOne(a => a.Merchant).WithMany().HasForeignKey(a => a.MerchantId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<MerchantRedirect>(e => e.HasOne<Merchant>().WithMany().HasForeignKey(a => a.MerchantId).OnDelete(DeleteBehavior.Restrict));
        model.Entity<StaffMember>(e =>
        {
            e.HasIndex(a => a.UserId).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<StaffActivity>(e =>
        {
            e.Property(a => a.Resource).HasMaxLength(32);
            e.Property(a => a.Action).HasMaxLength(64);
            e.Property(a => a.ChangesJson).HasMaxLength(8000);
            e.HasIndex(a => new { a.Resource, a.ResourceId, a.At });
            e.HasIndex(a => a.At);
            e.HasOne<User>().WithMany().HasForeignKey(a => a.ActorUserId).OnDelete(DeleteBehavior.SetNull);
        });
        model.Entity<StaffSavedView>(e =>
        {
            e.Property(a => a.Resource).HasMaxLength(32);
            e.Property(a => a.Name).HasMaxLength(60);
            e.Property(a => a.Url).HasMaxLength(2048);
            e.HasIndex(a => new { a.UserId, a.Resource, a.Name }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<SupportNotification>(e =>
        {
            e.HasIndex(a => new { a.UserId, a.ReadAt });
            e.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<SupportTicket>().WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<SupportTicketMessage>().WithMany().HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private List<Merchant> ChangedMerchantNames()
    {
        ChangeTracker.DetectChanges();
        return ChangeTracker.Entries<Merchant>().Where(e => e.State == EntityState.Added
            || e.State == EntityState.Modified && e.Property(m => m.NormalizedName).IsModified)
            .Select(e => e.Entity).ToList();
    }

    private List<MerchantAlias> RecordMerchantNames()
    {
        var merchants = ChangedMerchantNames();
        if (merchants.Count == 0) return [];
        var names = merchants.Select(m => m.NormalizedName).ToArray();
        return AddCanonicalNames(merchants, Set<MerchantAlias>().Where(a => names.Contains(a.NormalizedName)).ToList());
    }

    private async Task<List<MerchantAlias>> RecordMerchantNamesAsync(CancellationToken ct)
    {
        var merchants = ChangedMerchantNames();
        if (merchants.Count == 0) return [];
        var names = merchants.Select(m => m.NormalizedName).ToArray();
        return AddCanonicalNames(merchants, await Set<MerchantAlias>().Where(a => names.Contains(a.NormalizedName)).ToListAsync(ct));
    }

    private List<MerchantAlias> AddCanonicalNames(List<Merchant> merchants, List<MerchantAlias> existing)
    {
        var added = new List<MerchantAlias>();
        foreach (var merchant in merchants)
        {
            if (existing.Concat(Set<MerchantAlias>().Local).Any(a => a.NormalizedName == merchant.NormalizedName && a.MerchantId == merchant.Id)) continue;
            var alias = new MerchantAlias { MerchantId = merchant.Id, Merchant = merchant, Name = merchant.Name,
                NormalizedName = merchant.NormalizedName, CreatedAt = DateTimeOffset.UtcNow };
            Add(alias);
            added.Add(alias);
        }
        return added;
    }
}
