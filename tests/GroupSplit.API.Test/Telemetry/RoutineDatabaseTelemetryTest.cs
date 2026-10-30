using System.Data;
using GroupSplit.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Moq;
using OpenTelemetry.Instrumentation.EntityFrameworkCore;

namespace GroupSplit.API.Test.Telemetry;

public class RoutineDatabaseTelemetryTest
{
    private const string Poll = "SELECT b.\"Id\" FROM \"BackgroundJob\" AS b\nWHERE (b.\"Status\" = 'Pending' AND b.\"AvailableAt\" <= @now) OR (b.\"Status\" = 'Running' AND b.\"LeaseExpiresAt\" <= @now1)\nORDER BY b.\"AvailableAt\", b.\"CreatedAt\", b.\"Id\"\nLIMIT 1";

    [Theory]
    [InlineData(" SELECT 1\n", true)]
    [InlineData(Poll, true)]
    [InlineData("SELECT 1 FROM \"User\"", false)]
    [InlineData("SELECT b.\"Id\" FROM \"BackgroundJob\" AS b WHERE b.\"Status\" = 'Pending'", false)]
    [InlineData("UPDATE \"BackgroundJob\" SET \"Status\" = 'Running'", false)]
    [InlineData("SELECT * FROM \"Transaction\"", false)]
    public void Only_known_probes_are_filtered(string sql, bool expected)
        => Assert.Equal(expected, RoutineDatabaseTelemetry.IsProbe(Command(sql)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Registered_instrumentation_filters_probes_only_when_enabled(bool enabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:SuppressRoutineDatabaseTraces"] = enabled.ToString(),
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = ""
        });
        builder.ConfigureOpenTelemetry();
        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<EntityFrameworkInstrumentationOptions>>().Value;
        Assert.Equal(!enabled, options.Filter?.Invoke("Npgsql.EntityFrameworkCore.PostgreSQL", Command(Poll)) ?? true);
        Assert.True(options.Filter?.Invoke("Npgsql.EntityFrameworkCore.PostgreSQL", Command("SELECT * FROM \"Transaction\"")) ?? true);
    }

    private static IDbCommand Command(string sql)
    {
        var command = new Mock<IDbCommand>();
        command.SetupGet(c => c.CommandText).Returns(sql);
        command.SetupGet(c => c.CommandType).Returns(CommandType.Text);
        return command.Object;
    }
}
