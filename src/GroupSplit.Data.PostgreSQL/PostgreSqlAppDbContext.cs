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

        // The provider's row verbatim, queryable when a field it holds is wanted later
        // without adding a column for it. Text on every other provider.
        modelBuilder.Entity<BankTransaction>(entity =>
        {
            entity.Property(row => row.RawJson).HasColumnType("jsonb");
        });
    }
}