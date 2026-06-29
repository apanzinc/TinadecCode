using System.Diagnostics.Metrics;

namespace TinadecCore.Tracing;

/// <summary>
/// Metric name constants for Agent Debug Studio.
/// </summary>
public static class MetricNames
{
    public const string AgentTurnDuration = "tinadec_agent_turn_duration";
    public const string AgentTurnsTotal = "tinadec_agent_turns_total";
    public const string ToolExecutionDuration = "tinadec_tool_execution_duration";
    public const string ToolExecutionsTotal = "tinadec_tool_executions_total";
    public const string ModelRequestDuration = "tinadec_model_request_duration";
    public const string ModelRequestsTotal = "tinadec_model_requests_total";
    public const string ModelTokensTotal = "tinadec_model_tokens_total";
    public const string ApprovalWaitDuration = "tinadec_approval_wait_duration";
    public const string ApprovalsTotal = "tinadec_approvals_total";
    public const string OrchestrationDuration = "tinadec_orchestration_duration";
    public const string OrchestrationRunsTotal = "tinadec_orchestration_runs_total";
    public const string SqliteQueryDuration = "tinadec_sqlite_query_duration";
    public const string SqliteQueriesTotal = "tinadec_sqlite_queries_total";
}

/// <summary>
/// Central Meter instance for all TinadecCore metrics.
/// </summary>
public sealed class TinadecMetrics
{
    public const string MeterName = "TinadecCore";

    private readonly Meter _meter;
    private readonly Histogram<double> _agentTurnDuration;
    private readonly Counter<long> _agentTurnsTotal;
    private readonly Histogram<double> _toolExecutionDuration;
    private readonly Counter<long> _toolExecutionsTotal;
    private readonly Histogram<double> _modelRequestDuration;
    private readonly Counter<long> _modelRequestsTotal;
    private readonly Counter<long> _modelTokensTotal;
    private readonly Histogram<double> _approvalWaitDuration;
    private readonly Counter<long> _approvalsTotal;
    private readonly Histogram<double> _orchestrationDuration;
    private readonly Counter<long> _orchestrationRunsTotal;
    private readonly Histogram<double> _sqliteQueryDuration;
    private readonly Counter<long> _sqliteQueriesTotal;

    public TinadecMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        _agentTurnDuration = _meter.CreateHistogram<double>(MetricNames.AgentTurnDuration, "ms", "Agent turn duration");
        _agentTurnsTotal = _meter.CreateCounter<long>(MetricNames.AgentTurnsTotal, "runs", "Agent turn total count");
        _toolExecutionDuration =
            _meter.CreateHistogram<double>(MetricNames.ToolExecutionDuration, "ms", "Tool execution duration");
        _toolExecutionsTotal =
            _meter.CreateCounter<long>(MetricNames.ToolExecutionsTotal, "executions", "Tool execution total count");
        _modelRequestDuration =
            _meter.CreateHistogram<double>(MetricNames.ModelRequestDuration, "ms", "Model API request duration");
        _modelRequestsTotal =
            _meter.CreateCounter<long>(MetricNames.ModelRequestsTotal, "requests", "Model API request total count");
        _modelTokensTotal =
            _meter.CreateCounter<long>(MetricNames.ModelTokensTotal, "tokens", "Model token consumption");
        _approvalWaitDuration =
            _meter.CreateHistogram<double>(MetricNames.ApprovalWaitDuration, "ms", "Approval wait duration");
        _approvalsTotal = _meter.CreateCounter<long>(MetricNames.ApprovalsTotal, "approvals", "Approval total count");
        _orchestrationDuration =
            _meter.CreateHistogram<double>(MetricNames.OrchestrationDuration, "ms", "Orchestration full duration");
        _orchestrationRunsTotal =
            _meter.CreateCounter<long>(MetricNames.OrchestrationRunsTotal, "runs", "Orchestration run total count");
        _sqliteQueryDuration =
            _meter.CreateHistogram<double>(MetricNames.SqliteQueryDuration, "ms", "SQLite query duration");
        _sqliteQueriesTotal =
            _meter.CreateCounter<long>(MetricNames.SqliteQueriesTotal, "queries", "SQLite query total count");
    }

    // --- Agent Turn ---
    public void RecordAgentTurnDuration(double durationMs, string agentType, string status)
    {
        _agentTurnDuration.Record(durationMs, new KeyValuePair<string, object?>("agent_type", agentType),
            new KeyValuePair<string, object?>("status", status));
    }

    public void IncrementAgentTurns(string agentType, string status)
    {
        _agentTurnsTotal.Add(1, new KeyValuePair<string, object?>("agent_type", agentType),
            new KeyValuePair<string, object?>("status", status));
    }

    // --- Tool Execution ---
    public void RecordToolExecutionDuration(double durationMs, string toolId, string status)
    {
        _toolExecutionDuration.Record(durationMs, new KeyValuePair<string, object?>("tool_id", toolId),
            new KeyValuePair<string, object?>("status", status));
    }

    public void IncrementToolExecutions(string toolId, string status)
    {
        _toolExecutionsTotal.Add(1, new KeyValuePair<string, object?>("tool_id", toolId),
            new KeyValuePair<string, object?>("status", status));
    }

    // --- Model Request ---
    public void RecordModelRequestDuration(double durationMs, string model, string provider, string status)
    {
        _modelRequestDuration.Record(durationMs, new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("status", status));
    }

    public void IncrementModelRequests(string model, string provider, string status)
    {
        _modelRequestsTotal.Add(1, new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("status", status));
    }

    public void RecordModelTokens(long count, string direction)
    {
        _modelTokensTotal.Add(count, new KeyValuePair<string, object?>("direction", direction));
    }

    // --- Approval ---
    public void RecordApprovalWaitDuration(double durationMs, string kind, string decision)
    {
        _approvalWaitDuration.Record(durationMs, new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("decision", decision));
    }

    public void IncrementApprovals(string kind, string decision)
    {
        _approvalsTotal.Add(1, new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("decision", decision));
    }

    // --- Orchestration ---
    public void RecordOrchestrationDuration(double durationMs)
    {
        _orchestrationDuration.Record(durationMs);
    }

    public void IncrementOrchestrationRuns(string status)
    {
        _orchestrationRunsTotal.Add(1, new KeyValuePair<string, object?>("status", status));
    }

    // --- SQLite ---
    public void RecordSqliteQueryDuration(double durationMs, string table, string operation)
    {
        _sqliteQueryDuration.Record(durationMs, new KeyValuePair<string, object?>("table", table),
            new KeyValuePair<string, object?>("operation", operation));
    }

    public void IncrementSqliteQueries(string table, string operation)
    {
        _sqliteQueriesTotal.Add(1, new KeyValuePair<string, object?>("table", table),
            new KeyValuePair<string, object?>("operation", operation));
    }
}
