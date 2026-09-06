using GroupSplit.Data.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Data;

public class AppDbContext : DbContext, IDataProtectionKeyContext
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

    /// <summary>
    /// The Data Protection key ring, which protects the bank access tokens.
    /// </summary>
    /// <remarks>
    /// The only <c>DbSet</c> property on the context; everything else is reached through
    /// <c>Set&lt;T&gt;()</c>. <see cref="IDataProtectionKeyContext"/> leaves no choice --
    /// the framework's key repository reads this member by name -- so it is here and
    /// nothing else is. The keys live in the app database so that every API instance
    /// unprotects what any other protected, and so that a database reset takes the keys
    /// with the ciphertext they open.
    /// </remarks>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

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

                        // The database's, because EF inserts this row on its own behalf
                        // when a user is added to a group's members and never sees a
                        // constructor. Callers that know the moment overwrite it.
                        join.Property(membership => membership.JoinedAt)
                            .IsRequired()
                            .HasDefaultValueSql("CURRENT_TIMESTAMP")
                            .ValueGeneratedOnAdd();
                    });

            entity.HasIndex(user => user.Email).IsUnique();
        });

        modelBuilder.Entity<GroupInvitation>(entity =>
        {
            entity.Property(invitation => invitation.Email).HasMaxLength(128).IsRequired();
            entity.Property(invitation => invitation.InvitedAt).IsRequired();

            entity.HasOne(invitation => invitation.Group)
                .WithMany(group => group.Invitations)
                .HasForeignKey(invitation => invitation.GroupId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            // The inviter is a courtesy on the row the invitee reads ("Daniel asked you to
            // join"), so their account going away must not take the invitation with it.
            entity.HasOne(invitation => invitation.InvitedBy)
                .WithMany()
                .HasForeignKey(invitation => invitation.InvitedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // One standing invitation per address per group. A second would be a second
            // row in the group's "waiting on" list naming the same person.
            entity.HasIndex(invitation => new { invitation.GroupId, invitation.Email }).IsUnique();

            // How an invitee finds theirs: every group that has asked this address.
            entity.HasIndex(invitation => invitation.Email);
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

            // Filing copies, then links. Unlinking a bank deletes its rows, and the
            // expenses they became lose the link and nothing else -- they are history,
            // not an import.
            // One to one: a bank row files into at most one expense, and an expense came
            // from at most one row. The unique index EF builds for it lets the nulls
            // through, which is every typed transaction.
            entity.HasOne(transaction => transaction.BankTransaction)
                .WithOne(row => row.FiledAs)
                .HasForeignKey<Transaction>(transaction => transaction.BankTransactionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(transaction => transaction.DateTime);
            entity.HasIndex(transaction => transaction.Name);

            // Every expense listing filters on the group and orders by the date, and EF
            // adds the discriminator to that predicate itself, so the three travel
            // together often enough to be worth one index.
            entity.HasIndex(transaction => new { transaction.GroupId, transaction.DateTime });

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

        // EF does not index the discriminator on its own, and Set<Expense>() filters on
        // nothing else.
        //
        // After the leaves, not inside the base's configuration: until EF has been told
        // they exist there is no hierarchy, so there is no discriminator to index and
        // naming one asks for a shadow property with no type. This used to work from
        // inside the block only by accident -- RuleVersion.Transactions was an
        // ICollection<Expense>, so configuring it first taught EF the hierarchy on the way
        // past. Deleting RuleVersion took that accident with it.
        modelBuilder.Entity<Transaction>().HasIndex("Discriminator");

        // The import side of the seam. Nothing below joins the ledger except
        // Transaction.BankTransactionId above, and that points this way, not back.
        modelBuilder.Entity<BankConnection>(entity =>
        {
            entity.Property(connection => connection.Provider).HasMaxLength(32).IsRequired();
            entity.Property(connection => connection.ProviderItemId).HasMaxLength(128).IsRequired();
            entity.Property(connection => connection.InstitutionName).HasMaxLength(128).IsRequired();
            entity.Property(connection => connection.AccessTokenCiphertext).IsRequired();
            entity.Property(connection => connection.LinkedAt).IsRequired();

            // By name, not number: the first enum in the schema, and the precedent.
            entity.Property(connection => connection.Status)
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            // An item belongs to a person. Deleting the account takes the bank data with
            // it; there is nobody else it could belong to.
            entity.HasOne(connection => connection.User)
                .WithMany()
                .HasForeignKey(connection => connection.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            // A bank linked twice is one connection; the exchange finds this row and
            // answers with it rather than making a second.
            entity.HasIndex(connection => new { connection.Provider, connection.ProviderItemId }).IsUnique();

            // Every listing's filter.
            entity.HasIndex(connection => connection.UserId);
        });

        modelBuilder.Entity<LinkedAccount>(entity =>
        {
            entity.Property(account => account.ProviderAccountId).HasMaxLength(128).IsRequired();
            entity.Property(account => account.Name).HasMaxLength(128).IsRequired();
            entity.Property(account => account.Mask).HasMaxLength(8);
            entity.Property(account => account.Type).HasMaxLength(32).IsRequired();
            entity.Property(account => account.Subtype).HasMaxLength(32);

            entity.Property(account => account.Currency)
                .HasMaxLength(Currencies.CodeLength)
                .IsFixedLength()
                .IsRequired()
                .HasDefaultValue(Currencies.Default);

            entity.HasOne(account => account.Connection)
                .WithMany(connection => connection.Accounts)
                .HasForeignKey(account => account.BankConnectionId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(account => new { account.BankConnectionId, account.ProviderAccountId }).IsUnique();
        });

        modelBuilder.Entity<BankTransaction>(entity =>
        {
            entity.Property(row => row.ProviderTransactionId).HasMaxLength(128).IsRequired();
            entity.Property(row => row.Date).IsRequired();
            entity.Property(row => row.Amount).IsRequired().HasPrecision(18, 2);
            entity.Property(row => row.Description).HasMaxLength(256).IsRequired();
            entity.Property(row => row.MerchantName).HasMaxLength(128);
            entity.Property(row => row.ProviderCategory).HasMaxLength(64);
            entity.Property(row => row.Pending).IsRequired();
            entity.Property(row => row.RawJson).IsRequired();
            entity.Property(row => row.ImportedAt).IsRequired();

            entity.Property(row => row.Currency)
                .HasMaxLength(Currencies.CodeLength)
                .IsFixedLength()
                .IsRequired()
                .HasDefaultValue(Currencies.Default);

            entity.Property(row => row.Status)
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            entity.HasOne(row => row.Account)
                .WithMany(account => account.Transactions)
                .HasForeignKey(row => row.LinkedAccountId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            // The posted row remembers the pending one it replaced. Both are kept, so this
            // is a courtesy pointer and losing the old row must not take the new one.
            entity.HasOne(row => row.Replaces)
                .WithMany()
                .HasForeignKey(row => row.ReplacesId)
                .OnDelete(DeleteBehavior.SetNull);

            // The dedup key every upsert relies on: the provider's id, within the account.
            entity.HasIndex(row => new { row.LinkedAccountId, row.ProviderTransactionId }).IsUnique();

            // The inbox: this person's accounts, this status, newest first.
            entity.HasIndex(row => new { row.LinkedAccountId, row.Status, row.Date });
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