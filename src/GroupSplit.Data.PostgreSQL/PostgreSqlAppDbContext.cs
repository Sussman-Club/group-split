using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Data.PostgreSQL;

public class PostgreSqlAppDbContext(DbContextOptions<PostgreSqlAppDbContext> options) : AppDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.Property(transaction => transaction.DateTime)
                .HasColumnType("timestamp with time zone")
                .HasConversion(
                    appValue => appValue.ToUniversalTime(),
                    dbValue => dbValue
                );
        });

        // At most one current version per rule. A partial index because that is the only
        // way to say "unique among the rows that matter" -- every superseded version of a
        // rule shares its SplitRuleId, so an unfiltered unique index would forbid a second
        // edit. The alternative is a check in the service, which is a check two concurrent
        // edits can both pass.
        modelBuilder.Entity<SplitRuleVersion>()
            .HasIndex(version => version.SplitRuleId)
            .IsUnique()
            .HasFilter("\"SupersededAt\" IS NULL")
            .HasDatabaseName("IX_SplitRuleVersion_SplitRuleId_Current");

        // The provider's row verbatim, queryable when a field it holds is wanted later
        // without adding a column for it. Text on every other provider.
        modelBuilder.Entity<BankTransaction>(entity =>
        {
            entity.Property(row => row.RawJson).HasColumnType("jsonb");
        });
    }
}