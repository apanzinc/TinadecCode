using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TinadecCore.Tracing;

/// <summary>
/// Initializes and configures the OpenTelemetry TracerProvider and MeterProvider
/// for TinadecCore Agent Debug Studio.
/// </summary>
public sealed class AgentTracing
{
    public const string ServiceName = "tinadec-core";
    public const string ServiceVersion = "0.1.0";

    private TracerProvider? _tracerProvider;
    private MeterProvider? _meterProvider;

    public TracingOptions Options { get; }

    public AgentTracing(IConfiguration configuration)
    {
        Options = new TracingOptions();
        configuration.GetSection("TinadecTracing").Bind(Options);

        // Allow environment variable overrides
        var enabled = Environment.GetEnvironmentVariable("TINADEC_TRACING_ENABLED");
        if (enabled is not null)
            Options.Enabled = string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(enabled, "1", StringComparison.OrdinalIgnoreCase);

        var traceFile = Environment.GetEnvironmentVariable("TINADEC_TRACE_FILE");
        if (!string.IsNullOrWhiteSpace(traceFile)) Options.TraceFilePath = traceFile;

        var otlpTracesUrl = Environment.GetEnvironmentVariable("TINADEC_OTLP_TRACES_URL");
        if (!string.IsNullOrWhiteSpace(otlpTracesUrl)) Options.OtlpTracesUrl = otlpTracesUrl;

        var otlpMetricsUrl = Environment.GetEnvironmentVariable("TINADEC_OTLP_METRICS_URL");
        if (!string.IsNullOrWhiteSpace(otlpMetricsUrl)) Options.OtlpMetricsUrl = otlpMetricsUrl;
    }

    public void Initialize(TinadecMetrics metrics)
    {
        if (!Options.Enabled) return;

        var resource = ResourceBuilder.CreateDefault()
            .AddService(ServiceName, serviceVersion: ServiceVersion);

        // --- TracerProvider ---
        var tracerBuilder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(TinadecActivitySource.SourceName)
            .AddAspNetCoreInstrumentation(options =>
            {
                options.Filter = context =>
                    context.Request.Path.StartsWithSegments("/api/v1");
            })
            .AddHttpClientInstrumentation();

        if (Options.ConsoleExporterEnabled) tracerBuilder.AddConsoleExporter();

        if (!string.IsNullOrWhiteSpace(Options.OtlpTracesUrl))
            tracerBuilder.AddOtlpExporter(otlpOptions =>
            {
                otlpOptions.Endpoint = new Uri(Options.OtlpTracesUrl);
                otlpOptions.ExportProcessorType = ExportProcessorType.Batch;
                otlpOptions.BatchExportProcessorOptions = new BatchExportActivityProcessorOptions
                {
                    ScheduledDelayMilliseconds = Options.OtlpExportIntervalMs > 0 ? Options.OtlpExportIntervalMs : 10000
                };
            });

        // Always add the NDJSON file exporter
        tracerBuilder.AddProcessor(new NdjsonTraceExporter(new NdjsonTraceExporterOptions
        {
            FilePath = Options.TraceFilePath,
            MaxBytes = Options.TraceMaxBytes > 0 ? Options.TraceMaxBytes : 10 * 1024 * 1024,
            MaxFiles = Options.TraceMaxFiles > 0 ? Options.TraceMaxFiles : 10,
            BatchWindowMs = Options.TraceBatchWindowMs > 0 ? Options.TraceBatchWindowMs : 200
        }));

        _tracerProvider = tracerBuilder.Build();

        // --- MeterProvider ---
        var meterBuilder = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(TinadecMetrics.MeterName);

        if (!string.IsNullOrWhiteSpace(Options.OtlpMetricsUrl))
            meterBuilder.AddOtlpExporter(otlpOptions =>
            {
                otlpOptions.Endpoint = new Uri(Options.OtlpMetricsUrl);
            });

        if (Options.ConsoleExporterEnabled) meterBuilder.AddConsoleExporter();

        _meterProvider = meterBuilder.Build();
    }

    public void Shutdown()
    {
        _tracerProvider?.Shutdown();
        _meterProvider?.Shutdown();
        _tracerProvider?.Dispose();
        _meterProvider?.Dispose();
    }
}

/// <summary>
/// Configuration options for TinadecCore tracing.
/// </summary>
public sealed class TracingOptions
{
    public bool Enabled { get; set; } = true;
    public string TraceFilePath { get; set; } = "output/logs/core.trace.ndjson";
    public long TraceMaxBytes { get; set; } = 10 * 1024 * 1024;
    public int TraceMaxFiles { get; set; } = 10;
    public int TraceBatchWindowMs { get; set; } = 200;
    public string TraceMinLevel { get; set; } = "Info";
    public string? OtlpTracesUrl { get; set; }
    public string? OtlpMetricsUrl { get; set; }
    public string OtlpServiceName { get; set; } = "tinadec-core";
    public int OtlpExportIntervalMs { get; set; } = 10000;
    public bool ConsoleExporterEnabled { get; set; } = false;
}
