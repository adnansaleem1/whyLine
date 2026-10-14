#if WHYLINE_FOUNDRY
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using WhyLine.Agents.Pipeline;

namespace WhyLine.Web.Telemetry;

public sealed class TelemetryOptions
{
    public const string Section = "Telemetry";

    /// <summary>
    /// Application Insights connection string. Use the Application Insights resource connected to your Foundry project
    /// (Foundry portal → Tracing) so the newsroom's traces show up there. APPLICATIONINSIGHTS_CONNECTION_STRING also works.
    /// </summary>
    public string? ApplicationInsightsConnectionString { get; set; }

    /// <summary>Optional OTLP endpoint, e.g. http://localhost:4317 for the .NET Aspire dashboard running locally.</summary>
    public string? OtlpEndpoint { get; set; }
}

public static class TelemetrySetup
{
    /// <summary>
    /// Exports the newsroom's traces and metrics with OpenTelemetry: WhyLine's own spans (one trace per moment), the Agent
    /// Framework workflow and agent spans (GenAI semantic conventions) and ASP.NET Core. Does nothing unless an exporter
    /// is configured.
    /// </summary>
    public static string Configure(WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(TelemetryOptions.Section).Get<TelemetryOptions>() ?? new TelemetryOptions();
        var appInsights = FirstNonEmpty(options.ApplicationInsightsConnectionString,
            builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]);
        var otlp = FirstNonEmpty(options.OtlpEndpoint, builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        if (appInsights is null && otlp is null) return "off";

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("whyline", serviceVersion: "1.0"))
            .WithTracing(t => t
                .AddSource($"{NewsroomTelemetry.Prefix}.*")
                .AddSource("Microsoft.Agents.AI*")
                .AddSource("Experimental.Microsoft.Extensions.AI*"))
            .WithMetrics(m => m
                .AddMeter($"{NewsroomTelemetry.Prefix}.*")
                .AddMeter("Microsoft.Agents.AI*")
                .AddMeter("Experimental.Microsoft.Extensions.AI*"));

        var exporters = new List<string>();
        if (appInsights is not null)
        {
            otel.UseAzureMonitor(o => o.ConnectionString = appInsights);
            exporters.Add("Application Insights");
        }

        if (otlp is not null)
        {
            otel.WithTracing(t => t.AddOtlpExporter(o => o.Endpoint = new Uri(otlp)))
                .WithMetrics(m => m.AddOtlpExporter(o => o.Endpoint = new Uri(otlp)));
            exporters.Add($"OTLP ({otlp})");
        }

        return string.Join(" + ", exporters);
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
#endif
