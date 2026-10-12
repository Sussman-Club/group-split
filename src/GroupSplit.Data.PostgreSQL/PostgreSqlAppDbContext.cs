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
        // One join link in force per group: making a new one revokes the last, and two
        // members making one at the same moment no longer leave two standing. Expired links
        // count as in force here -- they are revoked when replaced, not when they lapse --
        // because "now" cannot appear in an index.
        modelBuilder.Entity<GroupJoinLink>()
            .HasIndex(link => link.GroupId)
            .IsUnique()
            .HasFilter("\"RevokedAt\" IS NULL")
            .HasDatabaseName("IX_GroupJoinLink_GroupId_Unrevoked");

        // A departure is three columns written together, and the query filter reads only
        // the first. These keep a half-written one -- a reason with no time, a time with no
        // reason, a remover on a standing membership -- out of the table instead of hidden
        // by the filter.
        modelBuilder.Entity<GroupMembership>().ToTable("GroupUser", table =>
        {
            table.HasCheckConstraint(
                "CK_GroupUser_LeftReason",
                "\"LeftReason\" IN ('Left', 'Removed', 'AccountDeleted')");

            table.HasCheckConstraint(
                "CK_GroupUser_Departure",
                "(\"LeftAt\" IS NULL) = (\"LeftReason\" IS NULL) AND (\"LeftAt\" IS NOT NULL OR \"LeftByUserId\" IS NULL)");
        });

        modelBuilder.Entity<GroupMembershipEvent>().ToTable(table => table.HasCheckConstraint(
            "CK_GroupMembershipEvent_Kind",
            "\"Kind\" IN ('Joined', 'Rejoined', 'Left', 'Removed', 'AccountDeleted', " +
            "'GuestAdded', 'GuestRemoved', 'GuestClaimed')"));

        // A guest's end and their link are each several columns written together, and the
        // query filter reads only EndedAt. These keep a half-written one out of the table: an
        // end with no reason, a reason with no end, a token with no issue date, and -- because
        // a link is a claim on a position -- a token left standing on a guest who has gone.
        modelBuilder.Entity<GroupGuest>().ToTable(table =>
        {
            table.HasCheckConstraint(
                "CK_GroupGuest_EndReason",
                "\"EndReason\" IN ('Removed', 'Claimed')");

            table.HasCheckConstraint(
                "CK_GroupGuest_End",
                "(\"EndedAt\" IS NULL) = (\"EndReason\" IS NULL) AND (\"EndedAt\" IS NOT NULL OR \"EndedByUserId\" IS NULL)");

            table.HasCheckConstraint(
                "CK_GroupGuest_Link",
                "(\"Token\" IS NULL) = (\"LinkIssuedAt\" IS NULL) AND (\"Token\" IS NOT NULL OR \"LinkIssuedByUserId\" IS NULL)");

            table.HasCheckConstraint(
                "CK_GroupGuest_EndedNoLink",
                "\"EndedAt\" IS NULL OR \"Token\" IS NULL");
        });

        // What each kind of user may hold. A stand-in has no address -- one would make it an
        // account somebody could sign in as -- and a deleted account has nothing left that
        // says who it was.
        modelBuilder.Entity<User>().ToTable(table =>
        {
            table.HasCheckConstraint(
                "CK_User_Kind",
                "\"Kind\" IN ('Account', 'StandIn', 'Deleted')");

            table.HasCheckConstraint(
                "CK_User_StandInHasNoEmail",
                "\"Kind\" <> 'StandIn' OR \"Email\" IS NULL");

            table.HasCheckConstraint(
                "CK_User_DeletedIsAnonymous",
                "\"Kind\" <> 'Deleted' OR (\"FirstName\" IS NULL AND \"LastName\" IS NULL AND \"Email\" IS NULL)");
        });

        modelBuilder.Entity<BankTransaction>(entity =>
        {
            entity.Property(row => row.RawJson).HasColumnType("jsonb");
        });

        // The merchant search is a substring match, LIKE '%lid%', which the unique btree on
        // the same column cannot serve: that one is for the sync's exact lookups. A trigram
        // index can, and the table is every place any account's bank has ever reported.
        // Named, because an unnamed index on the same column would be taken for the unique
        // one and replace it.
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<Merchant>()
            .HasIndex(merchant => merchant.NormalizedName, "IX_Merchant_NormalizedName_Trigram")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
    }
}