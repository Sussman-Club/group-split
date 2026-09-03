using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>
    /// Port the health endpoints answer on. Set by the AppHost from the resource's unpublished
    /// management endpoint; see <c>WithHealthEndpoints</c>.
    /// </summary>
    private const string ManagementPortConfigurationKey = "HealthChecks:Port";

    extension<TBuilder>(TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        public TBuilder AddServiceDefaults()
        {
            builder.ConfigureOpenTelemetry();

            builder.AddDefaultHealthChecks();

            builder.Services.AddServiceDiscovery();

            builder.Services.ConfigureHttpClientDefaults(http =>
            {
                // Turn on resilience by default
                http.AddStandardResilienceHandler();

                // Turn on service discovery by default
                http.AddServiceDiscovery();
            });

            // Uncomment the following to restrict the allowed schemes for service discovery.
            // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
            // {
            //     options.AllowedSchemes = ["https"];
            // });

            return builder;
        }

        public TBuilder ConfigureOpenTelemetry()
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });

            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation()
                        .AddMeter("Npgsql")
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation();
                })
                .WithTracing(tracing =>
                {
                    tracing.AddSource(builder.Environment.ApplicationName)
                        .AddAspNetCoreInstrumentation(options =>
                            // Exclude health check requests from tracing
                            options.Filter = context =>
                                !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                                && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                        )
                        // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                        //.AddGrpcClientInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddEntityFrameworkCoreInstrumentation();
                });

            builder.AddOpenTelemetryExporters();

            return builder;
        }

        private TBuilder AddOpenTelemetryExporters()
        {
            var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

            if (useOtlpExporter)
            {
                builder.Services.AddOpenTelemetry().UseOtlpExporter();
            }

            // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
            //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
            //{
            //    builder.Services.AddOpenTelemetry()
            //       .UseAzureMonitor();
            //}

            return builder;
        }

        public TBuilder AddDefaultHealthChecks()
        {
            builder.Services.AddHealthChecks()
                // Add a default liveness check to ensure app is responsive
                .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

            return builder;
        }
    }
    
    extension(WebApplication app)
    {
        /// <summary>
        /// Maps the health check endpoints on the management port.
        /// <para>
        /// A health endpoint answers with the status of every registered check, which tells
        /// its caller which dependencies exist and which of them are currently down. Rather
        /// than each service deciding whether publishing that is safe, the endpoints answer
        /// only on a management port that is never published -- the shape Keycloak uses for
        /// its own /health/ready on port 9000.
        /// </para>
        /// <para>
        /// The port is checked against the connection's local port, deliberately not with
        /// <c>RequireHost("*:port")</c>: that matches the Host header, which the caller sends
        /// and can therefore forge, so it lets a request on the published port reach these
        /// endpoints by claiming the management port. The local port is a property of the
        /// socket and cannot be spoofed.
        /// </para>
        /// <para>
        /// With no management port configured, which is what running one of these projects
        /// directly rather than through the AppHost looks like, this falls back to the
        /// framework template's rule and maps nothing outside development, rather than
        /// assuming an unknown port is safe to answer on.
        /// </para>
        /// </summary>
        public WebApplication MapDefaultEndpoints()
        {
            var managementPort = app.Configuration[ManagementPortConfigurationKey];
            var hasManagementPort = !string.IsNullOrWhiteSpace(managementPort);

            if (!hasManagementPort && !app.Environment.IsDevelopment())
            {
                return app;
            }

            if (hasManagementPort && int.TryParse(managementPort, out var port))
            {
                app.Use(async (context, next) =>
                {
                    if (IsHealthRequest(context) && context.Connection.LocalPort != port)
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await next();
                });
            }

            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });

            return app;
        }

        /// <summary>
        /// Redirects to HTTPS, except on the health endpoints.
        /// <para>
        /// Those answer on the management port, which is plain HTTP and has no TLS
        /// certificate, so redirecting them sends the prober to the public HTTPS port
        /// instead -- where <see cref="MapDefaultEndpoints"/>'s port guard correctly
        /// rejects it as a health request on the wrong port. The probe then sees a 404
        /// and the resource never reports healthy, which is not a symptom anything
        /// spells out: locally it looks like the app simply hangs on start-up, because
        /// everything downstream is still waiting for it.
        /// </para>
        /// <para>
        /// Only run mode shows this. Deployed, nothing gives these apps an HTTPS port,
        /// so <c>UseHttpsRedirection</c> logs a warning and passes every request
        /// through; the AppHost hands out an HTTPS endpoint, which switches it on.
        /// </para>
        /// <para>
        /// Exempting by path rather than by port costs nothing: a health request on any
        /// other port is already answered with a 404 by the guard, whichever scheme it
        /// arrived on.
        /// </para>
        /// </summary>
        public WebApplication UseDefaultHttpsRedirection()
        {
            app.UseWhen(
                context => !IsHealthRequest(context),
                branch => branch.UseHttpsRedirection());

            return app;
        }
    }

    /// <summary>
    /// Whether the request is for one of the health endpoints, by path alone. The
    /// port it arrived on is a separate question, answered in <c>MapDefaultEndpoints</c>.
    /// </summary>
    private static bool IsHealthRequest(HttpContext context) =>
        context.Request.Path.StartsWithSegments(HealthEndpointPath)
        || context.Request.Path.StartsWithSegments(AlivenessEndpointPath);
}
