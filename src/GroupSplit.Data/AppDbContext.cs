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

            entity.HasOne(rule => rule.Group)
                .WithMany(group => group.SplitRules)
                .HasForeignKey(rule => rule.GroupId)
                .IsRequired();

            // A group's rules are picked from a list by name, so two with the same name
            // are two the member cannot tell apart.
            entity.HasIndex(rule => new { rule.GroupId, rule.Name }).IsUnique();
        });

        // TPH, like Transaction: how a rule divides is which type it is -- answered by the
        // handler registered for that type, not by a Kind column and a switch. One table,
        // and the participants are declared once, on the middle layer that has them.
        //
        // Declared before the discriminator is indexed, because until EF has been told the
        // subtypes exist there is no hierarchy and therefore no discriminator to index.
        modelBuilder.Entity<WeightedSplitRule>();
        modelBuilder.Entity<EvenSplitRule>();

        modelBuilder.Entity<PayerSplitRule>();

        modelBuilder.Entity<PercentSplitRule>();

        modelBuilder.Entity<SharesSplitRule>();

        modelBuilder.Entity<SplitRule>().HasIndex("Discriminator");

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

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.Property(transaction => transaction.Amount).IsRequired().HasPrecision(18, 2);

            entity.Property(transaction => transaction.DateTime).IsRequired();
            entity.Property(transaction => transaction.Name).HasMaxLength(128).IsRequired();
            entity.Property(transaction => transaction.Description).HasMaxLength(256);

            entity.Property(transaction => transaction.Currency)
                .HasMaxLength(Currencies.CodeLength)
                .IsFixedLength()
                .IsRequired()
                .HasDefaultValue(Currencies.Default);

            entity.HasOne(transaction => transaction.User)
                .WithMany(user => user.Transactions)
                .HasForeignKey(transaction => transaction.UserId)
                .IsRequired();

            entity.HasOne(transaction => transaction.Group)
                .WithMany()
                .HasForeignKey(transaction => transaction.GroupId);

            entity.HasIndex(transaction => transaction.DateTime);
            entity.HasIndex(transaction => transaction.Name);

            // Every expense listing filters on the group and orders by the date, and EF
            // adds the discriminator to that predicate itself, so the three travel
            // together often enough to be worth one index.
            entity.HasIndex(transaction => new { transaction.GroupId, transaction.DateTime });

            // EF does not index the discriminator on its own, and Set<Expense>() filters
            // on nothing else.
            entity.HasIndex("Discriminator");
        });

        modelBuilder.Entity<Expense>(entity =>
        {
            // On the leaf, so the column is nullable in the table -- a transfer is not
            // filed under anything and never was.
            //
            // Restrict, not cascade: deleting a category must not take the group's spending
            // history with it. Clearing the expenses' category first is the caller's job,
            // and failing loudly is the right answer if they did not.
            entity.HasOne(expense => expense.Category)
                .WithMany()
                .HasForeignKey(expense => expense.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(expense => expense.CategoryId);
        });

        modelBuilder.Entity<Transfer>();

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