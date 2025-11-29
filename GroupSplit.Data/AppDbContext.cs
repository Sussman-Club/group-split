using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Data;

public class AppDbContext : DbContext
{
    /// <summary>
    /// Constructor for derived classes.
    /// </summary>
    /// <param name="options"></param>
    protected AppDbContext(DbContextOptions options) : base(options)
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : this((DbContextOptions)options)
    {
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.Property(user => user.FirstName).HasMaxLength(64);
            entity.Property(user => user.LastName).HasMaxLength(64);
            entity.Property(user => user.Email).HasMaxLength(128);

            entity.HasMany(user => user.Groups)
                .WithMany(group => group.Users);

            entity.HasOne(user => user.PersonalGroup)
                .WithOne()
                .HasForeignKey<User>("PersonalGroupId")
                .IsRequired();

            entity.HasIndex(user => user.Email).IsUnique();
        });

        modelBuilder.Entity<Group>(entity =>
        {
            entity.Property(group => group.Name).HasMaxLength(64).IsRequired();

            entity.HasIndex(group => group.Name);
        });

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

            entity.Property(transaction => transaction.DateTime).IsRequired();
            entity.Property(transaction => transaction.Name).HasMaxLength(128).IsRequired();
            entity.Property(transaction => transaction.Description).HasMaxLength(256);
            
            entity.HasOne(transaction => transaction.User)
                .WithMany(user => user.Transactions)
                .IsRequired();

            entity.HasOne(transaction => transaction.RuleVersion)
                .WithMany(group => group.Transactions)
                .IsRequired();
            
            entity.HasIndex(transaction => transaction.DateTime);
            entity.HasIndex(transaction => transaction.Name);
        });

        modelBuilder.Entity<UserIdentity>(entity =>
        {
            entity.Property(userIndentity => userIndentity.IdentityId)
                .IsRequired()
                .HasMaxLength(128);

            entity.HasIndex(userIdentity => userIdentity.IdentityId).IsUnique();

            entity.HasOne(userIdentity => userIdentity.User)
                .WithOne(user => user.Identity)
                .HasForeignKey<UserIdentity>("UserId")
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