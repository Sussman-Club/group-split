using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;

namespace GroupSplit.ServiceDefaults;

/// <summary>Small production metric set for request health and process resource trends.</summary>
public static class EssentialMetrics
{
    public static MetricStreamConfiguration Select(Instrument instrument)
        => instrument.Name switch
        {
            "http.server.request.duration" => new MetricStreamConfiguration
            {
                TagKeys = ["http.request.method", "http.response.status_code", "http.route", "error.type"]
            },
            "http.client.request.duration" => new MetricStreamConfiguration
            {
                TagKeys = ["http.request.method", "http.response.status_code", "server.address", "error.type"]
            },
            "dotnet.process.cpu.time" => new MetricStreamConfiguration { TagKeys = ["cpu.mode"] },
            "dotnet.process.memory.working_set" => new MetricStreamConfiguration { TagKeys = [] },
            "dotnet.gc.collections" or "dotnet.gc.last_collection.heap.size" =>
                new MetricStreamConfiguration { TagKeys = ["gc.heap.generation"] },
            _ => MetricStreamConfiguration.Drop
        };
}
