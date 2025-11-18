using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Data;

public class AppContext(DbContextOptions<AppContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasMany(user => user.Groups)
                .WithMany(group => group.Users);
        });

        modelBuilder.Entity<Group>();

        modelBuilder.Entity<Rule>(entity =>
        {
            entity.Property(rule => rule.Category).HasMaxLength(64).IsRequired();
            
            entity.HasOne(rule => rule.Group)
                .WithMany(group => group.Rules)
                .IsRequired();

            entity.HasMany(rule => rule.Versions)
                .WithOne(version => version.Rule)
                .IsRequired();
        });

        modelBuilder.Entity<RuleVersion>(entity =>
        {
            entity.Property(ruleVersion => ruleVersion.StartDate).IsRequired();

            entity.HasDiscriminator<string>("RuleType")
                .HasValue<PersonalRuleVersion>("personal")
                .HasValue<PercentRuleVersion>("percent");
        });

        modelBuilder.Entity<PersonalRuleVersion>();

        modelBuilder.Entity<PercentRuleVersion>();

        modelBuilder.Entity<PercentRuleUser>(entity =>
        {
            entity.Property(ruleUser => ruleUser.Percentage).IsRequired();

            entity.HasOne(ruleUser => ruleUser.User)
                .WithMany()
                .HasForeignKey(ruleUser => ruleUser.UserId)
                .IsRequired();

            entity.HasOne(ruleUser => ruleUser.RuleVersion)
                .WithMany(version => version.RuleUsers)
                .HasForeignKey(ruleUser => ruleUser.RuleVersionId)
                .IsRequired();

            entity.HasIndex(ruleUser => new { ruleUser.UserId, ruleUser.RuleVersionId }).IsUnique();
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.Property(transaction => transaction.Amount).IsRequired().HasPrecision(18, 2);

            entity.HasOne(transaction => transaction.User)
                .WithMany(user => user.Transactions)
                .IsRequired();

            entity.HasOne(transaction => transaction.Group)
                .WithMany(group => group.Transactions)
                .IsRequired();
        });

        var modelEntities = modelBuilder.Model.GetEntityTypes().ToList();

        // Ensure all entities inheriting from base Entity have a primary key on Id
        foreach (var entityType in modelEntities
                     .Where(t => typeof(Entity).IsAssignableFrom(t.ClrType) && t.ClrType != typeof(Entity)))
        {
            if (!typeof(Entity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            if (modelEntities.Any(parent => entityType.ClrType.IsAssignableTo(parent.ClrType)))
            {
                continue; // If there is a father the primary key is defined there.
            }

            var builder = modelBuilder.Entity(entityType.ClrType);
            builder.HasKey("Id");
        }

        base.OnModelCreating(modelBuilder);
    }
}