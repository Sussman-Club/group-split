using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data.PostgreSQL;
using GroupSplit.Data.PostgreSQL.Migrations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GroupSplit.AppHost.Test.Data;

/// <summary>
/// The migration that makes category and rule names unique whatever their case, run over a
/// database that already holds names differing only in case.
/// </summary>
/// <remarks>
/// The stack's own database has been migrated before any test runs, and holds no such names,
/// so it shows only that the migration applies to clean data. This one gets a database of
/// its own, migrated to just before, given the clashes production could have, and then
/// migrated the rest of the way. Dropped afterwards.
/// </remarks>
public class CaseInsensitiveNamesMigrationTest(AppHostFixture appHost)
{
    private const string Before = "20261009025728_AddPendingLoginDeletion";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 300_000)]
    public async Task Names_differing_only_in_case_are_renamed_and_the_index_then_refuses_them()
    {
        var server = await appHost.Application.GetConnectionStringAsync("db", Ct);

        var connectionString = new NpgsqlConnectionStringBuilder(server)
        {
            Database = $"case_names_{Guid.NewGuid():N}"
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<PostgreSqlAppDbContext>()
            .UseNpgsql(connectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(AppContextPostgreSqlFactory).Assembly.GetName().Name))
            .Options;

        await using var context = new PostgreSqlAppDbContext(options);

        try
        {
            await context.Database.MigrateAsync(Before, Ct);

            var groupId = Guid.NewGuid();
            var longName = new string('X', 64);

            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Group" ("Id", "Name") VALUES ({0}, 'Clashes');

                INSERT INTO "Category" ("Id", "GroupId", "Name") VALUES
                    (gen_random_uuid(), {0}, 'Food'),
                    (gen_random_uuid(), {0}, 'food'),
                    (gen_random_uuid(), {0}, 'FOOD'),
                    (gen_random_uuid(), {0}, 'Food (2)'),
                    (gen_random_uuid(), {0}, {1}),
                    (gen_random_uuid(), {0}, lower({1})),
                    (gen_random_uuid(), {0}, 'Rent');

                INSERT INTO "SplitRule" ("Id", "GroupId", "Name", "BuiltIn") VALUES
                    (gen_random_uuid(), {0}, 'all for ana', false),
                    (gen_random_uuid(), {0}, 'All for Ana', true);
                """, [groupId, longName], Ct);

            await context.Database.MigrateAsync(Ct);

            var categories = await Names(context, "Category", groupId);

            // Byte order, capitals first, keeps "FOOD"; the rest take the next free suffix in
            // that order, stepping over the "Food (2)" that was already there, and a name at
            // the column's length is shortened to fit its suffix.
            Assert.Equal(
                ["FOOD", "Food (2)", "Food (3)", "Rent", longName, "food (4)", $"{new string('x', 60)} (2)"],
                categories);

            // The built-in rule keeps its name over the one somebody wrote by hand.
            Assert.Equal(["All for Ana", "all for ana (2)"], await Names(context, "SplitRule", groupId));

            var refused = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(
                """INSERT INTO "Category" ("Id", "GroupId", "Name") VALUES (gen_random_uuid(), {0}, 'RENT')""",
                [groupId], Ct));

            Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
            Assert.Equal("IX_Category_GroupId_Name", refused.ConstraintName);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }

    private async Task<List<string>> Names(PostgreSqlAppDbContext context, string table, Guid groupId) =>
        await context.Database
            .SqlQueryRaw<string>($$"""SELECT "Name" AS "Value" FROM "{{table}}" WHERE "GroupId" = {0} ORDER BY "Name" COLLATE "C" """, groupId)
            .ToListAsync(Ct);
}
