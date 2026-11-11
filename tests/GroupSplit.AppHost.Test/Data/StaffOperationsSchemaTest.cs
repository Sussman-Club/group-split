using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.PostgreSQL;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;

using GroupSplit.AppHost.Test.Base;
using Aspire.Hosting.Testing;

namespace GroupSplit.AppHost.Test.Data;

public class StaffOperationsSchemaTest(AppHostFixture appHost)
{
    [Fact(Timeout = 120_000)]
    public async Task Migrations_merge_aliases_and_support_queries_work_with_PostgreSQL()
    {
        var cs = new NpgsqlConnectionStringBuilder(await appHost.Application.GetConnectionStringAsync("db", TestContext.Current.CancellationToken));
        // Create a disposable database so migrations and service operations never change app data.
        var database = "staff_verify_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(cs.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            cs.Database = database;
            var options = new DbContextOptionsBuilder<PostgreSqlAppDbContext>().UseNpgsql(cs.ConnectionString, b =>
                b.MigrationsAssembly("GroupSplit.Data.PostgreSQL.Migrations").EnableRetryOnFailure()).Options;
            await using var db = new PostgreSqlAppDbContext(options);
            await db.Database.MigrateAsync();
            await using (var check = new NpgsqlConnection(cs.ConnectionString))
            {
                await check.OpenAsync();
                await using var tableCheck = new NpgsqlCommand("SELECT to_regclass('\"SupportNotification\"') IS NULL", check);
                Assert.True((bool)(await tableCheck.ExecuteScalarAsync())!);
            }
            var user = new User { FirstName = "Staff", LastName = "Verification", Email = "verify@example.test", Kind = UserKind.Account };
            db.Add(user);
            await db.SaveChangesAsync();
            var merchantService = new MerchantService(new VerificationUser(user), db, TimeProvider.System);
            var source = await merchantService.CreateDirectory(new CreateMerchantRequest { Name = "Old shop" }, user.Id, default);
            var target = await merchantService.CreateDirectory(new CreateMerchantRequest { Name = "Canonical shop" }, user.Id, default);
            await merchantService.AddAlias(source.Id, "OLD SHOP #123", user.Id, default);
            var connection = new BankConnection { UserId = user.Id, Provider = "test", ProviderItemId = Guid.NewGuid().ToString(), InstitutionName = "Test bank", AccessTokenCiphertext = "test", LinkedAt = DateTimeOffset.UtcNow };
            var account = new LinkedAccount { Connection = connection, ProviderAccountId = "123", Name = "Test", Type = "depository" };
            var group = new Group { Name = "Verification" };
            db.Add(group);
            db.Add(account);
            var expense = new Expense { UserId = user.Id, Group = group, Amount = 12.34m, Currency = "USD", Name = "Purchase", DateTime = DateTimeOffset.UtcNow, MerchantId = source.Id };
            var row = new BankTransaction { Account = account, ProviderTransactionId = "verify-row", Date = DateOnly.FromDateTime(DateTime.UtcNow), Amount = 12.34m, Description = "Purchase", RawJson = "{}", ImportedAt = DateTimeOffset.UtcNow, MerchantId = source.Id };
            db.Add(expense);
            db.Add(row);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var ranked = await merchantService.ListDirectory(null, 0, "usage", default);
            Assert.Equal(source.Id, ranked.Items[0].Id);
            Assert.Equal(1, ranked.Items[0].TransactionCount);
            Assert.Equal(0, ranked.Items.Single(m => m.Id == target.Id).TransactionCount);
            var preview = await merchantService.PreviewMerge(source.Id, target.Id, default);
            Check(preview.Transactions == 1 && preview.BankRows == 1 && preview.Aliases == 2, "Merge preview counts");
            await merchantService.Merge(source.Id, new(target.Id, preview.Source.Version, preview.Target.Version, preview.Transactions, preview.BankRows, preview.Aliases), user.Id, default);
            db.ChangeTracker.Clear();
            Check((await db.Set<Expense>().SingleAsync()).MerchantId == target.Id, "Expense association preserved");
            Check((await db.Set<Expense>().SingleAsync()).Amount == 12.34m, "Expense amount preserved");
            Check((await db.Set<BankTransaction>().SingleAsync()).MerchantId == target.Id, "Bank association preserved");
            Check(await db.Set<MerchantAlias>().CountAsync(a => a.MerchantId == target.Id) == 3, "Aliases moved");
            Check((await merchantService.GetDirectory(source.Id)).Id == target.Id, "Cached IDs resolve");
            var resolved = await new MerchantDirectory(db, TimeProvider.System).ResolveAsync("old shop #123", null);
            Check(resolved?.Id == target.Id, "Bank import alias resolves canonical merchant");
            await merchantService.ListDirectory("old", 0, "usage", default);
            using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
            var team = new StaffTeamService(db, TimeProvider.System, cache);
            await team.RecordAccessAsync(user.Id, true, default);
            Check((await team.ListAsync(default)).Count == 1, "Staff directory translates");
            var users = new SupportUserService(db);
            Check((await users.SearchAsync("verify", default)).Count == 1, "User search translates");
            await users.GetAsync(user.Id, user.Id, default);
            var push = new GroupSplit.API.Services.Push.PushNotificationQueue(db,
                new Moq.Mock<GroupSplit.API.Services.BackgroundJobs.IBackgroundJobQueue>().Object, TimeProvider.System);
            var tickets = new SupportTicketService(db, TimeProvider.System, new SupportReportClassifier(null!, Microsoft.Extensions.Options.Options.Create(new GroupSplit.API.Services.Banking.Suggestions.FilingSuggestionOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<SupportReportClassifier>.Instance), team, push);
            var report = await tickets.CreateAsync(new CreateSupportReportRequest { Description = "Verify support reply" }, user.Id, default);
            await tickets.StaffReplyAsync(report.Id, new CreateSupportMessageRequest { Body = "Public reply" }, user.Id, default);
            Check((await tickets.GetAsync(report.Id, default)).Status == "WaitingForCustomer", "Waiting status and history constraints");
            await tickets.ListAsync(new SupportTicketQuery { Sort = "priority" }, default);
            await new StaffActivityService(db).ListAsync("Tickets", report.Id, 0, default);

        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
    private static void Check(bool value, string name) => Assert.True(value, name);
    private sealed class VerificationUser(User user) : ICurrentUser
    {
        public User User => user;
    }
}
