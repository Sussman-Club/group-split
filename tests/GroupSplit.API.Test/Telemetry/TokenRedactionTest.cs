using System.Collections.Concurrent;
using System.Diagnostics;
using GroupSplit.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace GroupSplit.API.Test.Telemetry;

/// <summary>
/// Invitation and join-link tokens never leave a service in its telemetry.
/// </summary>
/// <remarks>
/// The tokens are bearer credentials carried in the path, and the instrumentation records
/// paths as they were requested, so this is the difference between a dashboard that shows
/// traffic and one that hands out every invitation in flight. The end-to-end case runs a real
/// Kestrel server and a real <c>HttpClient</c>, because the spans and logs that matter are the
/// ones the framework writes, not ones a test would think to write itself.
/// </remarks>
public class TokenRedactionTest
{
    private const string Token = "s3cr3t-T0ken_value";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/invitations/claims/abc", "/invitations/claims/Redacted")]
    [InlineData("/invitations/claims/abc/decline", "/invitations/claims/Redacted/decline")]
    [InlineData("/invitations/links/abc", "/invitations/links/Redacted")]
    [InlineData("/invitations/links/abc/accept", "/invitations/links/Redacted/accept")]
    // The web app's forwarders, for the browser and for the native app.
    [InlineData("/api/invitations/links/abc", "/api/invitations/links/Redacted")]
    [InlineData("/native/api/invitations/claims/abc", "/native/api/invitations/claims/Redacted")]
    // The web pages the links are sent as.
    [InlineData("/join/abc", "/join/Redacted")]
    [InlineData("/claim/abc", "/claim/Redacted")]
    [InlineData("/Join/abc", "/Join/Redacted")]
    // A client span's url.full, and the query OpenTelemetry already redacts after it.
    [InlineData("https://api:8443/invitations/links/abc?x=Redacted", "https://api:8443/invitations/links/Redacted?x=Redacted")]
    [InlineData("https://groupsplit.example.com/join/abc#top", "https://groupsplit.example.com/join/Redacted#top")]
    // A log message with the URL in the middle of it.
    [InlineData("Start processing HTTP request GET https://api/invitations/claims/abc now", "Start processing HTTP request GET https://api/invitations/claims/Redacted now")]
    public void A_token_segment_is_redacted(string value, string expected)
    {
        Assert.Equal(expected, TokenRedaction.Redact(value));
    }

    [Theory]
    [InlineData("/invitations")]
    [InlineData("/invitations/claims")]
    [InlineData("/invitations/claims/")]
    [InlineData("/groups/0b0b8f7e-56a5-4f0e-9a6e-0d6c3c1f2f10/join-links")]
    [InlineData("/groups/0b0b8f7e-56a5-4f0e-9a6e-0d6c3c1f2f10/guests/0b0b8f7e-56a5-4f0e-9a6e-0d6c3c1f2f11/link")]
    [InlineData("/bank-connections/link-token")]
    [InlineData("/joiners/abc")]
    [InlineData("/invitations/links/{token}")]
    public void Everything_else_is_left_as_it_was(string value)
    {
        var redacted = TokenRedaction.Redact(value);

        // "{token}" is a route template, not a value; it is redacted too, and harmlessly.
        if (value.EndsWith("{token}", StringComparison.Ordinal))
        {
            Assert.Equal("/invitations/links/Redacted", redacted);
            return;
        }

        Assert.Same(value, redacted);
    }

    [Fact]
    public void A_finished_span_loses_the_token_from_its_url_tags_and_name()
    {
        using var activity = new Activity("test");
        activity.SetTag("url.path", $"/invitations/links/{Token}");
        activity.SetTag("url.full", $"https://api/invitations/links/{Token}");
        activity.SetTag("http.route", "/invitations/links/{token}");
        activity.DisplayName = $"GET /join/{Token}";

        new TokenRedaction.SpanProcessor().OnEnd(activity);

        Assert.Equal("/invitations/links/Redacted", activity.GetTagItem("url.path"));
        Assert.Equal("https://api/invitations/links/Redacted", activity.GetTagItem("url.full"));
        Assert.Equal("/invitations/links/{token}", activity.GetTagItem("http.route"));
        Assert.Equal("GET /join/Redacted", activity.DisplayName);
    }

    /// <summary>
    /// The service defaults' own telemetry setup, a server answering on the token routes, and
    /// an <c>HttpClient</c> calling it -- with an exporter registered after the defaults, where
    /// the OTLP exporter sits, recording everything it is handed.
    /// </summary>
    [Fact]
    public async Task No_exported_span_or_log_carries_the_token()
    {
        var spans = new ConcurrentQueue<string>();
        var logs = new ConcurrentQueue<string>();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.ConfigureOpenTelemetry();
        builder.Services.AddHttpClient();
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing =>
            tracing.AddProcessor(new SimpleActivityExportProcessor(new Collector<Activity>(spans, Describe))));
        builder.Services.ConfigureOpenTelemetryLoggerProvider(logging =>
            logging.AddProcessor(new SimpleLogRecordExportProcessor(new Collector<LogRecord>(logs, Describe))));

        await using var app = builder.Build();
        app.MapGet("/invitations/links/{token}", (string token) => Results.Ok());
        app.MapPost("/invitations/claims/{token}/decline", (string token) => Results.NoContent());
        app.MapGet("/join/{token}", (string token) => Results.Ok());
        await app.StartAsync(Ct);

        var origin = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient();

        (await client.GetAsync($"{origin}/invitations/links/{Token}", Ct)).EnsureSuccessStatusCode();
        (await client.PostAsync($"{origin}/invitations/claims/{Token}/decline", null, Ct)).EnsureSuccessStatusCode();
        (await client.GetAsync($"{origin}/join/{Token}?returnUrl=/join/{Token}", Ct)).EnsureSuccessStatusCode();

        // A server span ends after its response is on the wire, so only a stopped server has
        // finished all of them.
        await app.StopAsync(Ct);

        // Proof the spans and logs this is about were seen at all, redacted.
        Assert.Contains(spans, span => span.Contains("url.path=/invitations/links/Redacted", StringComparison.Ordinal));
        Assert.Contains(spans, span => span.Contains("/invitations/claims/Redacted/decline", StringComparison.Ordinal));
        Assert.Contains(spans, span => span.Contains("url.full=" + origin + "/join/Redacted", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.Contains("/invitations/links/Redacted", StringComparison.Ordinal));

        Assert.DoesNotContain(spans, span => span.Contains(Token, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, log => log.Contains(Token, StringComparison.Ordinal));
    }

    private static string Describe(Activity activity) =>
        string.Join(
            " ",
            activity.TagObjects
                .Select(tag => $"{tag.Key}={tag.Value}")
                .Prepend(activity.DisplayName));

    private static string Describe(LogRecord record) =>
        string.Join(
            " ",
            (record.Attributes ?? [])
                .Select(attribute => $"{attribute.Key}={attribute.Value}")
                .Prepend(record.Body)
                .Prepend(record.FormattedMessage));

    /// <summary>
    /// Writes each item down as text when it is exported. Text rather than the item, because
    /// log records are pooled and reused once the export returns.
    /// </summary>
    private sealed class Collector<T>(ConcurrentQueue<string> into, Func<T, string> describe) : BaseExporter<T>
        where T : class
    {
        public override ExportResult Export(in Batch<T> batch)
        {
            foreach (var item in batch)
            {
                into.Enqueue(describe(item));
            }

            return ExportResult.Success;
        }
    }
}
