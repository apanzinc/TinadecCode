using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TinadecCore.Services;
using TinadecCore.Storage;
using TinadecCore.Tracing;

namespace TinadecCore.Debug;

/// <summary>
/// REST API controller for the Agent Debug Studio.
/// Provides endpoints for querying traces, spans, metrics, and diagnostics.
/// </summary>
public static class DebugApiController
{
    public static IEndpointRouteBuilder MapDebugApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/debug");

        group.MapGet("/traces", GetTraces);
        group.MapGet("/traces/{traceId}", GetTrace);
        group.MapGet("/spans", GetSpans);
        group.MapGet("/metrics", GetMetrics);
        group.MapGet("/snapshot/{sessionId}", GetSnapshot);
        group.MapGet("/diagnostics", GetDiagnostics);
        group.MapGet("/processes", GetProcesses);

        return endpoints;
    }

    private static IResult GetTraces(
        string? sessionId,
        string? runId,
        string? name,
        string? status,
        double? minDurationMs,
        int? limit,
        int? offset,
        AgentTracing tracing)
    {
        if (!tracing.Options.Enabled) return Results.Ok(new { traces = Array.Empty<object>(), total_count = 0 });

        var records = ReadTraceFile(tracing.Options.TraceFilePath);
        var traceGroups = records
            .Where(r => string.IsNullOrEmpty(r.ParentSpanId))
            .Where(r => sessionId == null || r.Attributes.GetValueOrDefault("session_id")?.ToString() == sessionId)
            .Where(r => runId == null || r.Attributes.GetValueOrDefault("run_id")?.ToString() == runId)
            .Where(r => name == null || r.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Where(r => status == null || r.Status.Equals(status, StringComparison.OrdinalIgnoreCase))
            .Where(r => minDurationMs == null || r.DurationMs >= minDurationMs.Value)
            .Select(r =>
            {
                var childCount = records.Count(c => c.TraceId == r.TraceId && !string.IsNullOrEmpty(c.ParentSpanId));
                var errorCount = records.Count(c => c.TraceId == r.TraceId && c.Status == "ERROR");
                return new
                {
                    trace_id = r.TraceId,
                    root_span_name = r.Name,
                    root_span_duration_ms = r.DurationMs,
                    span_count = childCount + 1,
                    error_count = errorCount,
                    started_at = r.StartTimeUnixNano,
                    session_id = r.Attributes.GetValueOrDefault("session_id")?.ToString(),
                    run_id = r.Attributes.GetValueOrDefault("run_id")?.ToString()
                };
            })
            .ToList();

        var totalCount = traceGroups.Count;
        var effectiveLimit = Math.Min(limit ?? 50, 200);
        var effectiveOffset = offset ?? 0;
        var paged = traceGroups.Skip(effectiveOffset).Take(effectiveLimit);

        return Results.Ok(new { traces = paged, total_count = totalCount });
    }

    private static IResult GetTrace(string traceId, AgentTracing tracing)
    {
        if (!tracing.Options.Enabled)
            return Results.NotFound(new { code = "TRACING_DISABLED", message = "Tracing is not enabled." });

        var records = ReadTraceFile(tracing.Options.TraceFilePath);
        var traceRecords = records.Where(r => r.TraceId == traceId).ToList();

        if (traceRecords.Count == 0)
            return Results.NotFound(new { code = "TRACE_NOT_FOUND", message = $"Trace '{traceId}' was not found." });

        var rootSpan = BuildSpanTree(traceRecords, null);
        var resource = traceRecords.FirstOrDefault()?.Resource;

        return Results.Ok(new
        {
            trace_id = traceId,
            root_span = rootSpan,
            resource
        });
    }

    private static IResult GetSpans(
        string? name,
        string? status,
        double? minDurationMs,
        int? limit,
        AgentTracing tracing)
    {
        if (!tracing.Options.Enabled) return Results.Ok(new { spans = Array.Empty<object>(), total_count = 0 });

        var records = ReadTraceFile(tracing.Options.TraceFilePath);
        var filtered = records
            .Where(r => name == null || r.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Where(r => status == null || r.Status.Equals(status, StringComparison.OrdinalIgnoreCase))
            .Where(r => minDurationMs == null || r.DurationMs >= minDurationMs.Value)
            .Select(r => new
            {
                r.SpanId,
                r.ParentSpanId,
                r.Name,
                r.Kind,
                r.DurationMs,
                r.Status,
                r.Attributes,
                r.TraceId
            })
            .ToList();

        var totalCount = filtered.Count;
        var effectiveLimit = Math.Min(limit ?? 100, 500);

        return Results.Ok(new { spans = filtered.Take(effectiveLimit), total_count = totalCount });
    }

    private static IResult GetMetrics(
        string metricName,
        long? windowMs,
        long? bucketMs,
        AgentTracing tracing)
    {
        // Return a placeholder metrics response
        // In production, this would aggregate from the MeterProvider
        var window = windowMs ?? 3600000;
        var bucket = bucketMs ?? 60000;
        var bucketCount = (int)(window / bucket);

        return Results.Ok(new
        {
            metric_name = metricName,
            window_ms = window,
            bucket_ms = bucket,
            buckets = Enumerable.Range(0, bucketCount).Select(i => new
            {
                started_at = DateTime.UtcNow.AddMilliseconds(-window + i * bucket).ToString("O"),
                ended_at = DateTime.UtcNow.AddMilliseconds(-window + (i + 1) * bucket).ToString("O"),
                count = 0,
                sum = 0.0,
                min = 0.0,
                max = 0.0,
                p50 = 0.0,
                p95 = 0.0,
                p99 = 0.0
            }),
            summary = new
            {
                total_count = 0,
                total_sum = 0.0,
                avg = 0.0,
                min = 0.0,
                max = 0.0,
                p50 = 0.0,
                p95 = 0.0,
                p99 = 0.0
            }
        });
    }

    private static IResult GetSnapshot(string sessionId, CoreStore store)
    {
        var snapshot = store.GetOrchestrationSnapshot(sessionId);
        return Results.Ok(snapshot);
    }

    private static IResult GetDiagnostics(TraceDiagnosticService diagnostics)
    {
        var report = diagnostics.GenerateReport();
        return Results.Ok(report);
    }

    private static IResult GetProcesses()
    {
        var process = Process.GetCurrentProcess();
        return Results.Ok(new
        {
            pid = process.Id,
            process_name = process.ProcessName,
            working_set_bytes = process.WorkingSet64,
            private_bytes = process.PrivateMemorySize64,
            gc_heap_bytes = GC.GetTotalMemory(false),
            thread_count = process.Threads.Count,
            handle_count = process.HandleCount,
            cpu_time_ms = process.TotalProcessorTime.TotalMilliseconds,
            uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).ToString(),
            dotnet_version = Environment.Version.ToString(),
            machine_name = Environment.MachineName,
            processor_count = Environment.ProcessorCount,
            os_version = Environment.OSVersion.ToString()
        });
    }

    // --- Helper: Read NDJSON trace file ---

    private static List<TraceFileRecord> ReadTraceFile(string filePath)
    {
        var records = new List<TraceFileRecord>();

        try
        {
            if (!File.Exists(filePath)) return records;

            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<TraceFileRecord>(line, TinadecJson.Options);
                    if (record is not null) records.Add(record);
                }
                catch
                {
                    // Skip malformed lines
                }
            }
        }
        catch
        {
            // Return empty list on file read errors
        }

        return records;
    }

    // --- Helper: Build span tree from flat records ---

    private static object BuildSpanTree(List<TraceFileRecord> records, string? parentSpanId)
    {
        var children = records.Where(r => r.ParentSpanId == parentSpanId).ToList();
        return children.Select(r => new
        {
            r.SpanId,
            r.ParentSpanId,
            r.Name,
            r.Kind,
            start_time = r.StartTimeUnixNano,
            end_time = r.EndTimeUnixNano,
            r.DurationMs,
            r.Status,
            r.StatusMessage,
            r.Attributes,
            r.Events,
            children = BuildSpanTree(records, r.SpanId),
            links = Array.Empty<object>()
        });
    }
}

// --- NDJSON file record model (for reading) ---

internal sealed class TraceFileRecord
{
    public string TraceId { get; set; } = "";
    public string SpanId { get; set; } = "";
    public string? ParentSpanId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "INTERNAL";
    public string StartTimeUnixNano { get; set; } = "";
    public string EndTimeUnixNano { get; set; } = "";
    public double DurationMs { get; set; }
    public string Status { get; set; } = "OK";
    public string? StatusMessage { get; set; }
    public Dictionary<string, object?> Attributes { get; set; } = new();
    public object[]? Events { get; set; }
    public object? Resource { get; set; }
}
