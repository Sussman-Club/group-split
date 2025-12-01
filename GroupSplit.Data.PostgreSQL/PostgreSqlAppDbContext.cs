using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Data.PostgreSQL;

public class PostgreSqlAppDbContext(DbContextOptions<PostgreSqlAppDbContext> options) : AppDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RuleVersion>(entity =>
        {
            entity.Property(ruleVersion => ruleVersion.StartDateTime)
                .HasColumnType("timestamp with time zone")
                .HasConversion(
                    appValue => appValue.ToUniversalTime(),
                    dbValue => dbValue
                );

            entity.Property(rv => rv.EndDateTime)
                .HasColumnType("timestamp with time zone")
                .HasConversion(
                    appValue => appValue.HasValue ? appValue.Value.ToUniversalTime() : appValue,
                    dbValue => dbValue
                );
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.Property(transaction => transaction.DateTime)
                .HasColumnType("timestamp with time zone")
                .HasConversion(
                    appValue => appValue.ToUniversalTime(),
                    dbValue => dbValue
                );
        });
    }
}