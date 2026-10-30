using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GroupSplit.Data.PostgreSQL.Migrations.Migrations;

[DbContext(typeof(PostgreSqlAppDbContext))]
[Migration("20261030210000_BackgroundJobWakeup")]
public sealed class BackgroundJobWakeup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql(BackgroundJobNotifications.CreateSql);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql(BackgroundJobNotifications.DropSql);
}
