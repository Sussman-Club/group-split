using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace GroupSplit.API.Test.Telemetry;

public class EssentialMetricsTest
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Production_profile_keeps_core_metrics_and_bounds_request_dimensions(bool enabled)
    {
        var meterName = $"EssentialMetricsTest.{Guid.NewGuid()}";
        using var meter = new Meter(meterName);
        var exporter = new Collector(meterName);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:EssentialMetricsOnly"] = enabled.ToString(),
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = ""
        });
        builder.ConfigureOpenTelemetry();
        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics
            .AddMeter(meterName)
            .AddReader(new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: 3600000)));
        using var services = builder.Services.BuildServiceProvider();
        var provider = services.GetRequiredService<MeterProvider>();

        var requests = meter.CreateHistogram<double>("http.server.request.duration", "s");
        requests.Record(0.1, new("http.route", "/groups/{id}"), new("http.response.status_code", 200), new("url.path", "/groups/one"));
        requests.Record(0.2, new("http.route", "/groups/{id}"), new("http.response.status_code", 200), new("url.path", "/groups/two"));
        requests.Record(0.3, new("http.route", "/groups/{id}"), new("http.response.status_code", 500), new("url.path", "/groups/three"));
        foreach (var name in new[] { "http.client.request.duration", "dotnet.process.cpu.time", "dotnet.process.memory.working_set", "dotnet.gc.collections", "dotnet.gc.last_collection.heap.size", "detailed.test.metric" })
            meter.CreateCounter<long>(name).Add(1);
        Assert.True(provider.ForceFlush());

        Assert.Equal(enabled ? 6 : 7, exporter.PointCounts.Count);
        Assert.Equal(enabled ? 2 : 3, exporter.PointCounts["http.server.request.duration"]);
        Assert.Equal(!enabled, exporter.PointCounts.ContainsKey("detailed.test.metric"));
        Assert.Equal(!enabled, exporter.HasRawPath);
        Assert.True(exporter.HasErrorStatus);
    }

    private sealed class Collector(string meterName) : BaseExporter<Metric>
    {
        public ConcurrentDictionary<string, int> PointCounts { get; } = new();
        public bool HasRawPath { get; private set; }
        public bool HasErrorStatus { get; private set; }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MeterName != meterName) continue;
                var count = 0;
                foreach (var point in metric.GetMetricPoints())
                {
                    count++;
                    foreach (var tag in point.Tags)
                    {
                        if (tag.Key == "url.path") HasRawPath = true;
                        if (tag.Key == "http.response.status_code" && Equals(tag.Value, 500)) HasErrorStatus = true;
                    }
                }
                PointCounts[metric.Name] = count;
            }
            return ExportResult.Success;
        }
    }
}
