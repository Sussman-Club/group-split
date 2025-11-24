using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Seeders.Base;
using Microsoft.Extensions.Options;

namespace GroupSplit.Seeder.Seeders;

public class GroupSeeder(AppDbContext db, ILogger<GroupSeeder> logger, IOptions<SeederOptions> options)
    : DbContextJsonSeeder<Group, Group, AppDbContext>(db, options.Value.Paths.Groups, logger);