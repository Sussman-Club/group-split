using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;

namespace GroupSplit.Seeder.Seeders;

public class GroupSeeder(AppDbContext db, ILogger<GroupSeeder> logger, ISeedDataSource<GroupSeedDto> source)
    : AppDbContextSeeder<Group, GroupSeedDto>(db, source, logger);