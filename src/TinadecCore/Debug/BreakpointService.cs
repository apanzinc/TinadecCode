using System.Collections.Concurrent;

namespace TinadecCore.Debug;

/// <summary>
/// Manages breakpoints for the Agent Debug Studio.
/// Supports condition-based breakpoints with configurable actions.
/// </summary>
public sealed class BreakpointService
{
    private readonly ConcurrentDictionary<string, Breakpoint> _breakpoints = new();
    private int _nextId = 1;

    /// <summary>
    /// Create a new breakpoint.
    /// </summary>
    public Breakpoint Create(CreateBreakpointRequest request)
    {
        var id = $"bp_{Interlocked.Increment(ref _nextId)}";
        var breakpoint = new Breakpoint
        {
            Id = id,
            ConditionType = request.ConditionType,
            Condition = request.Condition,
            Action = request.Action,
            ActionParams = request.ActionParams,
            HitCount = 0,
            Enabled = true,
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        _breakpoints[id] = breakpoint;
        return breakpoint;
    }

    /// <summary>
    /// Delete a breakpoint by ID.
    /// </summary>
    public bool Delete(string id)
    {
        return _breakpoints.TryRemove(id, out _);
    }

    /// <summary>
    /// List all breakpoints.
    /// </summary>
    public IReadOnlyList<Breakpoint> ListAll()
    {
        return _breakpoints.Values.ToList();
    }

    /// <summary>
    /// Evaluate whether any breakpoint's condition is met for the given context.
    /// Returns matching breakpoints (if any) and increments their hit count.
    /// </summary>
    public IReadOnlyList<Breakpoint> Evaluate(string conditionType, Dictionary<string, object?> context)
    {
        var hits = new List<Breakpoint>();

        foreach (var bp in _breakpoints.Values)
        {
            if (!bp.Enabled || bp.ConditionType != conditionType) continue;

            if (EvaluateCondition(bp, context))
            {
                bp.HitCount++;
                hits.Add(bp);
            }
        }

        return hits;
    }

    private static bool EvaluateCondition(Breakpoint bp, Dictionary<string, object?> context)
    {
        foreach (var condition in bp.Condition)
        {
            if (!context.TryGetValue(condition.Key, out var value)) return false;

            if (value is null && condition.Value is null) continue;
            if (value is null || condition.Value is null) return false;

            if (!string.Equals(value.ToString(), condition.Value?.ToString(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}

// --- Breakpoint Models ---

public sealed class Breakpoint
{
    public string Id { get; set; } = "";
    public string ConditionType { get; set; } = "";
    public Dictionary<string, object?> Condition { get; set; } = new();
    public string Action { get; set; } = "";
    public Dictionary<string, object?>? ActionParams { get; set; }
    public int HitCount { get; set; }
    public bool Enabled { get; set; }
    public string CreatedAt { get; set; } = "";
}

public sealed class CreateBreakpointRequest
{
    public string ConditionType { get; set; } =
        ""; // on_tool_call, on_approval, on_agent_error, on_token_budget, on_state_change, on_sub_agent_spawn

    public Dictionary<string, object?> Condition { get; set; } = new();
    public string Action { get; set; } = ""; // pause, log, auto_approve, inject_response
    public Dictionary<string, object?>? ActionParams { get; set; }
}
