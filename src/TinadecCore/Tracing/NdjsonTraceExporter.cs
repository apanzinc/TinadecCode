using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTelemetry;

namespace TinadecCore.Tracing;

/// <summary>
/// Custom OpenTelemetry processor that exports completed activities (spans)
/// to a local NDJSON file, compatible with Codex rollout-trace format.
/// Supports file rotation with configurable size and count limits.
/// </summary>
public sealed class NdjsonTraceExporter : BaseProcessor<Activity>
{
    private readonly string _filePath;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly int _batchWindowMs;
    private readonly ConcurrentQueue<JsonDocument> _queue = new();
    private readonly Timer _flushTimer;
    private readonly object _writeLock = new();

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private long _seq;
    private FileStream? _fileStream;
    private StreamWriter? _writer;

    public NdjsonTraceExporter(NdjsonTraceExporterOptions options)
    {
        _filePath = options.FilePath ?? "output/logs/core.trace.ndjson";
        _maxBytes = options.MaxBytes > 0 ? options.MaxBytes : 10 * 1024 * 1024; // 10MB default
        _maxFiles = options.MaxFiles > 0 ? options.MaxFiles : 10;
        _batchWindowMs = options.BatchWindowMs > 0 ? options.BatchWindowMs : 200;

        // Ensure directory exists
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        OpenFile();
        _flushTimer = new Timer(_ => FlushQueue(), null, _batchWindowMs, _batchWindowMs);
    }

    public override void OnEnd(Activity activity)
    {
        if (activity.Source.Name != TinadecActivitySource.SourceName) return;

        var record = ConvertToTraceRecord(activity);
        var json = JsonSerializer.SerializeToDocument(record, _jsonOptions);
        _queue.Enqueue(json);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flushTimer.Dispose();
            FlushQueue();
            CloseFile();
        }

        base.Dispose(disposing);
    }

    private void FlushQueue()
    {
        if (_queue.IsEmpty) return;

        lock (_writeLock)
        {
            while (_queue.TryDequeue(out var json))
                try
                {
                    _writer?.WriteLine(json.RootElement.GetRawText());
                }
                catch
                {
                    // Silently ignore write errors to avoid crashing the process
                }

            try
            {
                _writer?.Flush();
                _fileStream?.Flush();
            }
            catch
            {
                // Silently ignore flush errors
            }

            RotateIfNeeded();
        }
    }

    private void OpenFile()
    {
        _fileStream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(_fileStream, Encoding.UTF8);
    }

    private void CloseFile()
    {
        _writer?.Dispose();
        _fileStream?.Dispose();
        _writer = null;
        _fileStream = null;
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (_fileStream is null || _fileStream.Length < _maxBytes) return;

            CloseFile();

            // Rotate: file.9 -> delete, file.8 -> file.9, ..., file -> file.1
            var basePath = _filePath;
            for (var i = _maxFiles - 1; i >= 1; i--)
            {
                var olderPath = $"{basePath}.{i}";
                var newerPath = i == 1 ? basePath : $"{basePath}.{i - 1}";

                if (!File.Exists(newerPath)) continue;

                if (i == _maxFiles - 1)
                    if (File.Exists(olderPath))
                        File.Delete(olderPath);

                if (File.Exists(newerPath))
                {
                    var targetPath = $"{basePath}.{i}";
                    File.Move(newerPath, targetPath, true);
                }
            }

            OpenFile();
        }
        catch
        {
            // If rotation fails, try to reopen the file
            try { CloseFile(); }
            catch { }

            try { OpenFile(); }
            catch { }
        }
    }

    private TraceRecord ConvertToTraceRecord(Activity activity)
    {
        var attributes = new Dictionary<string, object?>();
        foreach (var tag in activity.TagObjects) attributes[tag.Key] = tag.Value;

        var events = activity.Events.Select(e =>
        {
            var evtAttrs = new Dictionary<string, object?>();
            foreach (var tag in e.Tags) evtAttrs[tag.Key] = tag.Value;
            return new TraceEvent
            {
                Name = e.Name,
                TimeUnixNano = e.Timestamp.UtcDateTime.ToUnixTimeNanoseconds().ToString(),
                Attributes = evtAttrs
            };
        }).ToArray();

        return new TraceRecord
        {
            SchemaVersion = "1.0.0",
            Seq = Interlocked.Increment(ref _seq),
            TraceId = activity.TraceId.ToString(),
            SpanId = activity.SpanId.ToString(),
            ParentSpanId = activity.ParentSpanId.ToString(),
            Name = activity.DisplayName,
            Kind = activity.Kind.ToString(),
            StartTimeUnixNano = activity.StartTimeUtc.ToUnixTimeNanoseconds().ToString(),
            EndTimeUnixNano = activity.Duration == TimeSpan.Zero
                ? activity.StartTimeUtc.ToUnixTimeNanoseconds().ToString()
                : (activity.StartTimeUtc + activity.Duration).ToUnixTimeNanoseconds().ToString(),
            DurationMs = activity.Duration.TotalMilliseconds,
            Status = activity.Status == ActivityStatusCode.Error ? "ERROR" : "OK",
            StatusMessage = activity.StatusDescription,
            Attributes = attributes,
            Events = events.Length > 0 ? events : null,
            Resource = new TraceResource
            {
                ServiceName = "tinadec-core",
                ServiceVersion = "0.1.0"
            }
        };
    }
}

public sealed class NdjsonTraceExporterOptions
{
    public string? FilePath { get; set; }
    public long MaxBytes { get; set; }
    public int MaxFiles { get; set; }
    public int BatchWindowMs { get; set; }
}

// --- Trace Record Models ---

internal sealed class TraceRecord
{
    public string SchemaVersion { get; set; } = "1.0.0";
    public long Seq { get; set; }
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
    public TraceEvent[]? Events { get; set; }
    public TraceResource Resource { get; set; } = new();
}

internal sealed class TraceEvent
{
    public string Name { get; set; } = "";
    public string TimeUnixNano { get; set; } = "";
    public Dictionary<string, object?> Attributes { get; set; } = new();
}

internal sealed class TraceResource
{
    public string ServiceName { get; set; } = "";
    public string ServiceVersion { get; set; } = "";
}

internal static class DateTimeExtensions
{
    public static long ToUnixTimeNanoseconds(this DateTime utc)
    {
        return (utc.Ticks - DateTime.UnixEpoch.Ticks) * 100; // 1 tick = 100 nanoseconds
    }
}
