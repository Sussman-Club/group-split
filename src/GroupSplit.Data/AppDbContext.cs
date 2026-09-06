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

            // The join carries a payload now -- who has archived which group -- so it is a
            // type rather than one EF keeps to itself. Pinned to the table and column names
            // EF had already chosen, so this is an added column and not a moved membership.
            entity.HasMany(user => user.Groups)
                .WithMany(group => group.Users)
                .UsingEntity<GroupMembership>(
                    right => right.HasOne<Group>().WithMany().HasForeignKey(membership => membership.GroupId),
                    left => left.HasOne<User>().WithMany().HasForeignKey(membership => membership.UserId),
                    join =>
                    {
                        join.ToTable("GroupUser");
                        join.Property(membership => membership.GroupId).HasColumnName("GroupsId");
                        join.Property(membership => membership.UserId).HasColumnName("UsersId");
                        join.HasKey(membership => new { membership.GroupId, membership.UserId });
                    });

            entity.HasOne(user => user.PersonalGroup)
                .WithOne()
                .HasForeignKey<User>("PersonalGroupId")
                .IsRequired();

            entity.HasIndex(user => user.Email).IsUnique();
        });

        modelBuilder.Entity<Group>(entity =>
        {
            entity.Property(group => group.Name).HasMaxLength(64).IsRequired();

            entity.Property(group => group.Currency)
                .HasMaxLength(Currencies.CodeLength)
                .IsFixedLength()
                .IsRequired()
                .HasDefaultValue(Currencies.Default);

            entity.HasIndex(group => group.Name);
        });

        modelBuilder.Entity<SplitRule>(entity =>
        {
            entity.Property(rule => rule.Name).HasMaxLength(64).IsRequired();

            entity.Property(rule => rule.Kind).IsRequired();

            entity.HasOne(rule => rule.Group)
                .WithMany(group => group.SplitRules)
                .HasForeignKey(rule => rule.GroupId)
                .IsRequired();

            // A group's rules are picked from a list by name, so two with the same name
            // are two the member cannot tell apart.
            entity.HasIndex(rule => new { rule.GroupId, rule.Name }).IsUnique();
        });

        modelBuilder.Entity<SplitRuleParticipant>(entity =>
        {
            entity.Property(participant => participant.Weight).IsRequired();

            entity.HasOne(participant => participant.SplitRule)
                .WithMany(rule => rule.Participants)
                .HasForeignKey(participant => participant.SplitRuleId)
                .IsRequired();

            entity.HasOne(participant => participant.User)
                .WithMany()
                .HasForeignKey(participant => participant.UserId)
                .IsRequired();

            entity.HasIndex(participant => new { participant.SplitRuleId, participant.UserId }).IsUnique();
        });

        modelBuilder.Entity<Category>(entity =>
        {
            entity.Property(category => category.Name).HasMaxLength(64).IsRequired();

            entity.HasOne(category => category.Group)
                .WithMany(group => group.Categories)
                .HasForeignKey(category => category.GroupId)
                .IsRequired();

            // Restrict rather than cascade or set-null: a rule several categories default
            // to is exactly the rule somebody will try to delete, and silently emptying
            // their defaults would change how every future expense in them is split
            // without saying so.
            entity.HasOne(category => category.DefaultSplitRule)
                .WithMany()
                .HasForeignKey(category => category.DefaultSplitRuleId)
                .OnDelete(DeleteBehavior.Restrict);

            // Inherited from the constraint Rule carried on (GroupId, Category), which is
            // the same statement now that the label has a table of its own.
            entity.HasIndex(category => new { category.GroupId, category.Name }).IsUnique();
        });

        modelBuilder.Entity<TransactionSplit>(entity =>
        {
            entity.Property(split => split.Amount).IsRequired().HasPrecision(18, 2);

            entity.HasOne(split => split.Transaction)
                .WithMany(transaction => transaction.Splits)
                .HasForeignKey(split => split.TransactionId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(split => split.User)
                .WithMany()
                .HasForeignKey(split => split.UserId)
                .IsRequired();

            // One row per person per transaction: a second would be a second opinion about
            // what they owed.
            entity.HasIndex(split => new { split.TransactionId, split.UserId }).IsUnique();

            // The balance query's access path -- every split this person is named in.
            entity.HasIndex(split => split.UserId);
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
            
            entity.HasIndex(rule => rule.Category);
            entity.HasIndex(rule => new { rule.GroupId, rule.Category }).IsUnique();
        });

        modelBuilder.Entity<RuleVersion>(entity =>
        {
            entity.Property(ruleVersion => ruleVersion.StartDateTime).IsRequired();
            
            entity.Property(ruleVersion => ruleVersion.EndDateTime);

            entity.UseTptMappingStrategy();
        });

        modelBuilder.Entity<PersonalRuleVersion>();

        modelBuilder.Entity<PercentRuleVersion>();
        
        modelBuilder.Entity<SharesRuleVersion>();

        modelBuilder.Entity<SettlementRuleVersion>(entity =>
        {
            entity.HasOne(settlement => settlement.OtherUser)
                .WithMany();

            entity.HasIndex(settlement => settlement.OtherUserId);
        });
        
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
        
        modelBuilder.Entity<SharesRuleUser>(entity =>
        {
            entity.Property(ruleUser => ruleUser.Shares).IsRequired();

            entity.HasOne(ruleUser => ruleUser.User)
                .WithMany()
                .HasForeignKey(ruleUser => ruleUser.UserId)
                .IsRequired();

            entity.HasOne(ruleUser => ruleUser.RuleVersion)
                .WithMany(version => version.SharedRuleUsers)
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