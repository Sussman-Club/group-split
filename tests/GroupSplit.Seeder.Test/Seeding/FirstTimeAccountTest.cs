using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.DataSources;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.Seeder.Test.Seeding;

public class FirstTimeAccountTest
{
    [Fact]
    public async Task Demo_account_starts_with_only_its_empty_personal_group()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "../../..", "src/GroupSplit.Seeder/SeedData/users.json"));
        var source = new JsonArrayFileDataSource<UserSeedDto>(path, NullLogger<JsonArrayFileDataSource<UserSeedDto>>.Instance);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var seeder = new UserSeeder(db, NullLogger<UserSeeder>.Instance, source);
        var ct = TestContext.Current.CancellationToken;
        await seeder.SeedAsync(ct);
        await seeder.SeedAsync(ct);
        var user = await db.Set<User>().Include(x => x.Groups).SingleAsync(x => x.Email == "nueva@test.com", ct);
        Assert.Equal(user.Id, Assert.Single(user.Groups).Id);
        Assert.Empty(await db.Set<Transaction>().Where(x => x.UserId == user.Id).ToListAsync(ct));
        Assert.Null(user.Onboarding.GroupId);
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string file = "") => file;
}
