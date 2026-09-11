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
            entity.Property(invitation => invitation.Name).HasMaxLength(64).IsRequired();
            entity.Property(invitation => invitation.Token).HasMaxLength(64).IsRequired();
            entity.Property(invitation => invitation.InvitedAt).IsRequired();

            entity.HasOne(invitation => invitation.Group)
                .WithMany(group => group.Invitations)
                .HasForeignKey(invitation => invitation.GroupId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            // Required, and restricted: this is who the group's money is recorded against
            // while the address has not answered, so an invitation without one could hold
            // shares belonging to nobody -- and losing the row would take them with it.
            entity.HasOne(invitation => invitation.Participant)
                .WithMany()
                .HasForeignKey(invitation => invitation.ParticipantUserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);

            // The inviter is a courtesy on the row the invitee reads ("Daniel asked you to
            // join"), so their account going away must not take the invitation with it.
            entity.HasOne(invitation => invitation.InvitedBy)
                .WithMany()
                .HasForeignKey(invitation => invitation.InvitedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // The token is the whole lookup: somebody opens a URL and the only thing in it
            // is this. Unique because two rows answering to one token would be two
            // positions one link could claim.
            entity.HasIndex(invitation => invitation.Token).IsUnique();

            // Read on every membership question a group asks -- who may be given a share,
            // whose balance to show, whether this person has joined -- both by group and by
            // participant alone.
            //
            // Unique on the pair, and that is the constraint that replaced one standing
            // invitation per address: a stand-in is made for one invitation and nothing
            // else, so a second row naming it would be two invitations for one person's
            // money. Names are deliberately *not* unique -- two people can be called Dani.
            entity.HasIndex(invitation => new { invitation.GroupId, invitation.ParticipantUserId }).IsUnique();

            entity.HasIndex(invitation => invitation.ParticipantUserId);
        });

        modelBuilder.Entity<InvitationOpened>(entity =>
        {
            entity.HasKey(opened => new { opened.InvitationId, opened.UserId });

            entity.Property(opened => opened.OpenedAt).IsRequired();

            // Cascade from both parents, the way a bank match dismissal does, and for the
            // same reason: the row is a fact about a pair and has nothing left to say once
            // either half of it is gone. Answering an invitation deletes it, and this goes
            // with it -- which is also what takes the invitation out of the invitee's list.
            //
            // Postgres is happy with two cascade paths into a leaf table.
            entity.HasOne<GroupInvitation>()
                .WithMany()
                .HasForeignKey(opened => opened.InvitationId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(opened => opened.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            // The one read there is: what this account has opened and not yet answered.
            entity.HasIndex(opened => opened.UserId);
        });

        modelBuilder.Entity<GroupJoinLink>(entity =>
        {
            entity.Property(link => link.Token).HasMaxLength(64).IsRequired();
            entity.Property(link => link.CreatedAt).IsRequired();
            entity.Property(link => link.ExpiresAt).IsRequired();

            entity.HasOne(link => link.Group)
                .WithMany(group => group.JoinLinks)
                .HasForeignKey(link => link.GroupId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(link => link.CreatedBy)
                .WithMany()
                .HasForeignKey(link => link.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // The token is the whole lookup: somebody opens a URL and the only thing in it
            // is this. Unique because two rows answering to one token would be two groups
            // one link could let somebody into.
            entity.HasIndex(link => link.Token).IsUnique();

            // A group's own list, newest first, which is how the current link is found.
            entity.HasIndex(link => new { link.GroupId, link.CreatedAt });
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

        modelBuilder.Entity<SplitRuleVersion>(entity =>
        {
            entity.HasOne(version => version.SplitRule)
                .WithMany(rule => rule.Versions)
                .HasForeignKey(version => version.SplitRuleId)
                .IsRequired()
                // A rule's versions go when the rule does. Nothing is lost by that, because
                // a rule with a transaction pointing at any of its versions cannot be
                // deleted at all -- Transaction below restricts it, and the service says so
                // in words first.
                .OnDelete(DeleteBehavior.Cascade);

            // "What does this rule say now" is the commonest read there is -- every
            // category listing and every expense created under one -- and it is the
            // version whose SupersededAt is null. That there is only ever one of those is
            // a partial unique index, which is provider-specific and therefore lives in
            // PostgreSqlAppDbContext.
            entity.HasIndex(version => new { version.SplitRuleId, version.SupersededAt });
        });

        // TPH, like Transaction: how a rule divides is which type it is -- answered by the
        // handler registered for that type, not by a Kind column and a switch. One table,
        // and the participants are declared once, on the middle layer that has them.
        //
        // Declared before the discriminator is indexed, because until EF has been told the
        // subtypes exist there is no hierarchy and therefore no discriminator to index.
        modelBuilder.Entity<WeightedSplitRuleVersion>();
        modelBuilder.Entity<EvenSplitRuleVersion>();

        modelBuilder.Entity<PayerSplitRuleVersion>();

        modelBuilder.Entity<PercentSplitRuleVersion>();

        modelBuilder.Entity<SharesSplitRuleVersion>();

        // Not optional, and quiet about it if forgotten: EF discovers a derived type only
        // where the model names it, so an unregistered kind is not a mapping error -- it is
        // stored as its base, read back as its base, and dispatched to the wrong handler.
        // Which looks like a rule that divides evenly for no reason anybody can see.
        modelBuilder.Entity<ItemizedSplitRuleVersion>();

        modelBuilder.Entity<SplitRuleVersion>().HasIndex("Discriminator");

        modelBuilder.Entity<SplitRuleParticipant>(entity =>
        {
            entity.Property(participant => participant.Weight).IsRequired();

            entity.HasOne(participant => participant.SplitRuleVersion)
                .WithMany(version => version.Participants)
                .HasForeignKey(participant => participant.SplitRuleVersionId)
                .IsRequired();

            entity.HasOne(participant => participant.User)
                .WithMany()
                .HasForeignKey(participant => participant.UserId)
                .IsRequired();

            entity.HasIndex(participant => new { participant.SplitRuleVersionId, participant.UserId }).IsUnique();
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

            // Restrict, not cascade: a merchant row is shared by every expense filed
            // against it, so deleting one must not take somebody's history. Nothing deletes
            // merchants today, and failing loudly is the right answer if something starts.
            entity.HasOne(transaction => transaction.Merchant)
                .WithMany(merchant => merchant.Transactions)
                .HasForeignKey(transaction => transaction.MerchantId)
                .OnDelete(DeleteBehavior.Restrict);

            // Restrict, not cascade or set-null: the version a transaction was divided by
            // is the only record of which division produced its splits, so deleting it
            // would quietly turn a recalculable expense into an unexplained set of amounts.
            // A rule nothing has been recorded under still deletes, versions and all.
            entity.HasOne(transaction => transaction.SplitRuleVersion)
                .WithMany(version => version.Transactions)
                .HasForeignKey(transaction => transaction.SplitRuleVersionId)
                .OnDelete(DeleteBehavior.Restrict);

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

            // Non-nullable with a false default: every connection that already exists was
            // never re-keyed, which is exactly what false says.
            entity.Property(connection => connection.AccountsRekeyed).IsRequired().HasDefaultValue(false);

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

        modelBuilder.Entity<PendingBankLink>(entity =>
        {
            entity.Property(link => link.Provider).HasMaxLength(32).IsRequired();
            entity.Property(link => link.StartedAt).IsRequired();
            entity.Property(link => link.Attempts).IsRequired();

            // Both nullable and both protected: a row carries the public token until the
            // exchange happens and the item afterwards, and never usefully both.
            entity.Property(link => link.PublicTokenCiphertext);
            entity.Property(link => link.ItemCiphertext);

            // Same rule as a connection: a link belongs to the person who started it, and
            // deleting the account takes it with them.
            entity.HasOne(link => link.User)
                .WithMany()
                .HasForeignKey(link => link.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            // What the sweep reads: the oldest links still worth another attempt.
            entity.HasIndex(link => new { link.Attempts, link.StartedAt });
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
            entity.Property(row => row.ProviderCategoryDetailed).HasMaxLength(96);
            entity.Property(row => row.PaymentChannel).HasMaxLength(32);
            entity.Property(row => row.City).HasMaxLength(64);
            entity.Property(row => row.CategoryIconUrl).HasMaxLength(512);
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

            // Restrict for the same reason the ledger's side is: the row is shared.
            entity.HasOne(row => row.Merchant)
                .WithMany()
                .HasForeignKey(row => row.MerchantId)
                .OnDelete(DeleteBehavior.Restrict);

            // The dedup key every upsert relies on: the provider's id, within the account.
            entity.HasIndex(row => new { row.LinkedAccountId, row.ProviderTransactionId }).IsUnique();

            // The inbox: this person's accounts, and this status.
            //
            // The date on the end no longer serves the listing. Both the span it narrows by
            // and the order it is read in are the authorized date falling back to the
            // posting one, which is a COALESCE no index on either column alone can answer --
            // so a page of a span is filtered and sorted, over a few hundred rows an
            // account, rather than read in stored order. Cheaper than the stored column that
            // would restore it, and the date still earns its place here for the replay
            // BankSyncService does over an account's posted rows.
            entity.HasIndex(row => new { row.LinkedAccountId, row.Status, row.Date });
        });

        modelBuilder.Entity<BankMatchDismissal>(entity =>
        {
            entity.Property(dismissal => dismissal.DismissedAt).IsRequired();

            // Cascade from both parents. The row is an opinion about a pair, so it has
            // nothing left to say once either half of the pair is gone -- and Postgres is
            // happy with two cascade paths into a leaf table.
            entity.HasOne(dismissal => dismissal.BankTransaction)
                .WithMany()
                .HasForeignKey(dismissal => dismissal.BankTransactionId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(dismissal => dismissal.Transaction)
                .WithMany()
                .HasForeignKey(dismissal => dismissal.TransactionId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            // Dismissing the same pair twice is the same answer, not a second one; and the
            // index is also how every suggestion query asks whether it has been answered.
            entity.HasIndex(dismissal => new { dismissal.BankTransactionId, dismissal.TransactionId }).IsUnique();
        });

        modelBuilder.Entity<Merchant>(entity =>
        {
            entity.Property(merchant => merchant.Name).HasMaxLength(128).IsRequired();
            entity.Property(merchant => merchant.NormalizedName).HasMaxLength(128).IsRequired();
            entity.Property(merchant => merchant.LogoUrl).HasMaxLength(512);
            entity.Property(merchant => merchant.FirstSeenAt).IsRequired();

            // One row per place. The resolver reads this index before every insert, so a
            // sync that meets Lidl on forty rows creates one merchant and links forty.
            entity.HasIndex(merchant => merchant.NormalizedName).IsUnique();
        });

        modelBuilder.Entity<Receipt>(entity =>
        {
            // The same precision the ledger keeps, and for the same reason: the division
            // truncates to the cent and hands the leftover to the payer, so a figure
            // carrying a third decimal would push a fraction nobody can pay onto somebody
            // on every bill it appears in.
            entity.Property(receipt => receipt.Subtotal).IsRequired().HasPrecision(18, 2);
            entity.Property(receipt => receipt.Tax).IsRequired().HasPrecision(18, 2);
            entity.Property(receipt => receipt.Tip).IsRequired().HasPrecision(18, 2);
            entity.Property(receipt => receipt.Total).IsRequired().HasPrecision(18, 2);

            // Both cascade, and that is only coherent because a receipt has exactly one
            // owner at a time: it belongs to the bank row it was typed against until filing
            // hands it to the expense, which clears the other link. TransactionService.Create
            // and InboxService.Link are where the hand-over happens.
            //
            // Set-null was the mistake it replaces. Deleting an expense nulled the link, and
            // a bill somebody typed onto a typed expense then had nothing left to belong to
            // -- so the check constraint refused the delete and the expense could never be
            // removed at all. Unlinking a bank did the same to an unfiled bill, taking the
            // whole sync save down with it.
            //
            // Nothing is lost by cascading. Where the bill came from is recorded on the
            // ledger, in Transaction.BankTransactionId, which is where that fact has always
            // lived; and a bill is one expense's, so an expense that is gone takes it.
            entity.HasOne(receipt => receipt.Expense)
                .WithOne(expense => expense.Receipt)
                .HasForeignKey<Receipt>(receipt => receipt.ExpenseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(receipt => receipt.BankTransaction)
                .WithOne()
                .HasForeignKey<Receipt>(receipt => receipt.BankTransactionId)
                .OnDelete(DeleteBehavior.Cascade);

            // One bill per expense and one per bank row. The nulls are the ordinary case on
            // each -- an unfiled receipt has no expense, a typed one has no bank row -- and
            // a unique index lets them through, exactly as the one behind
            // Transaction.BankTransactionId does.
            entity.HasIndex(receipt => receipt.ExpenseId).IsUnique();
            entity.HasIndex(receipt => receipt.BankTransactionId).IsUnique();

            // A receipt belongs to exactly one thing -- an expense, or the bank row it was
            // typed against. That invariant is stated to the database rather than here, in
            // PostgreSqlAppDbContext, because a check constraint is the only thing that can
            // hold it and it needs the provider's SQL to say so.
        });

        modelBuilder.Entity<ReceiptItem>(entity =>
        {
            entity.Property(item => item.Name).HasMaxLength(128).IsRequired();
            entity.Property(item => item.UnitPrice).IsRequired().HasPrecision(18, 2);
            entity.Property(item => item.TotalPrice).IsRequired().HasPrecision(18, 2);

            // Three decimals, unlike the money columns: a quantity is weighed as well as
            // counted, and 0.250 kg is an ordinary line on a bill.
            entity.Property(item => item.Quantity).IsRequired().HasPrecision(18, 3);

            // Stored as the number, like every other enum here. A line's division is read
            // on every division of the bill and never searched on, so it needs no index.
            entity.Property(item => item.Division).IsRequired();

            entity.HasOne(item => item.Receipt)
                .WithMany(receipt => receipt.Items)
                .HasForeignKey(item => item.ReceiptId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(item => item.ReceiptId);
        });

        modelBuilder.Entity<ReceiptItemClaim>(entity =>
        {
            entity.HasOne(claim => claim.ReceiptItem)
                .WithMany(item => item.Claims)
                .HasForeignKey(claim => claim.ReceiptItemId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not cascade: a claim is what produced somebody's share of the
            // expense, and losing it silently would leave shares no longer explained by the
            // bill they came from. Removing a member from a group is an act that already
            // has to reckon with their balance; this makes it reckon with their claims too.
            entity.HasOne(claim => claim.User)
                .WithMany()
                .HasForeignKey(claim => claim.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);

            // One row per person per line: a second would be two opinions about how much of
            // the bottle they had. Sharing is expressed by the weight, not by more rows.
            entity.HasIndex(claim => new { claim.ReceiptItemId, claim.UserId }).IsUnique();
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