using System.Diagnostics;

namespace TinadecCore.Debug;

/// <summary>
/// Provides process-level resource diagnostics for the Agent Debug Studio.
/// Mirrors T3 Code's ProcessDiagnostics utility.
/// </summary>
public sealed class ProcessDiagnosticsService
{
    public ProcessDiagnosticsSnapshot GetSnapshot()
    {
        var process = Process.GetCurrentProcess();

        return new ProcessDiagnosticsSnapshot
        {
            Pid = process.Id,
            ProcessName = process.ProcessName,
            WorkingSetBytes = process.WorkingSet64,
            PrivateBytes = process.PrivateMemorySize64,
            GcHeapBytes = GC.GetTotalMemory(false),
            GcGen0Collections = GC.CollectionCount(0),
            GcGen1Collections = GC.CollectionCount(1),
            GcGen2Collections = GC.CollectionCount(2),
            ThreadCount = process.Threads.Count,
            HandleCount = process.HandleCount,
            TotalProcessorTimeMs = process.TotalProcessorTime.TotalMilliseconds,
            UserProcessorTimeMs = process.UserProcessorTime.TotalMilliseconds,
            StartTimeUtc = process.StartTime.ToUniversalTime().ToString("O"),
            Uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).ToString(),
            DotnetVersion = Environment.Version.ToString(),
            MachineName = Environment.MachineName,
            ProcessorCount = Environment.ProcessorCount,
            OsVersion = Environment.OSVersion.ToString(),
            Is64BitProcess = Environment.Is64BitProcess,
            Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
            CommandLine = Environment.CommandLine
        };
    }
}

public sealed class ProcessDiagnosticsSnapshot
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
    public long GcHeapBytes { get; set; }
    public int GcGen0Collections { get; set; }
    public int GcGen1Collections { get; set; }
    public int GcGen2Collections { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public double TotalProcessorTimeMs { get; set; }
    public double UserProcessorTimeMs { get; set; }
    public string StartTimeUtc { get; set; } = "";
    public string Uptime { get; set; } = "";
    public string DotnetVersion { get; set; } = "";
    public string MachineName { get; set; } = "";
    public int ProcessorCount { get; set; }
    public string OsVersion { get; set; } = "";
    public bool Is64BitProcess { get; set; }
    public bool Is64BitOperatingSystem { get; set; }
    public string CommandLine { get; set; } = "";
}
