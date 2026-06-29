using System.Diagnostics;
using System.Text.Json.Nodes;
using TinadecCore.Services;
using TinadecCore.Storage;
using TinadecCore.Tracing;

namespace TinadecCore.Debug;

/// <summary>
/// Core simulation logic for the Agent Debug Studio.
/// Supports injecting messages, model responses, tool results,
/// forcing approval decisions, and patching agent state.
/// All simulation operations create spans marked with simulated=true.
/// </summary>
public sealed class SimulationService
{
    private readonly CoreStore _store;
    private readonly EventHub _events;
    private readonly OrchestratorService _orchestrator;
    private readonly BreakpointService _breakpoints;

    public SimulationService(
        CoreStore store,
        EventHub events,
        OrchestratorService orchestrator,
        BreakpointService breakpoints)
    {
        _store = store;
        _events = events;
        _orchestrator = orchestrator;
        _breakpoints = breakpoints;
    }

    public SimulateMessageResponse InjectMessage(SimulateMessageRequest request)
    {
        using var activity = TinadecActivitySource.Instance.StartActivity(SpanNames.AgentTurn);
        activity?
            .SetTag(SpanAttrs.SessionId, request.SessionId)
            .SetTag(SpanAttrs.Simulated, true);

        var userMessage = _store.AddMessage(request.SessionId, "user", $"[SIMULATED] {request.Content}");

        _events.Publish(_store.AppendNewEvent("simulation.message.injected", request.SessionId, new JsonObject
        {
            ["message_id"] = userMessage.Id,
            ["simulated"] = true
        }, ["simulation", "agent.message"]));

        if (!request.SkipModelCall)
        {
            // Trigger real orchestration
            var orchestration = _orchestrator.CreateRunForMessage(request.SessionId, userMessage.Id, request.Content);
        }
        else if (!string.IsNullOrWhiteSpace(request.MockModelResponse))
        {
            // Inject mock model response
            var assistantMessage =
                _store.AddMessage(request.SessionId, "assistant", $"[SIMULATED] {request.MockModelResponse}");
            _events.Publish(_store.AppendNewEvent("simulation.model_response.injected", request.SessionId,
                new JsonObject
                {
                    ["message_id"] = assistantMessage.Id,
                    ["simulated"] = true
                }, ["simulation", "agent.message"]));
        }

        return new SimulateMessageResponse
        {
            SimulationId = Guid.NewGuid().ToString(),
            TraceId = Activity.Current?.TraceId.ToString() ?? "",
            Simulated = true
        };
    }

    public SimulateMessageResponse InjectModelResponse(SimulateModelResponseRequest request)
    {
        using var activity = TinadecActivitySource.Instance.StartActivity(SpanNames.AgentInference);
        activity?
            .SetTag(SpanAttrs.SessionId, request.SessionId)
            .SetTag(SpanAttrs.Simulated, true);

        var assistantMessage = _store.AddMessage(request.SessionId, "assistant", $"[SIMULATED] {request.Content}");

        _events.Publish(_store.AppendNewEvent("simulation.model_response.injected", request.SessionId, new JsonObject
        {
            ["message_id"] = assistantMessage.Id,
            ["simulated"] = true
        }, ["simulation", "agent.message"]));

        return new SimulateMessageResponse
        {
            SimulationId = Guid.NewGuid().ToString(),
            TraceId = Activity.Current?.TraceId.ToString() ?? "",
            Simulated = true
        };
    }

    public SimulateMessageResponse InjectToolResult(SimulateToolResultRequest request)
    {
        using var activity = TinadecActivitySource.Instance.StartActivity(SpanNames.AgentToolExecution);
        activity?
            .SetTag(SpanAttrs.RunId, request.RunId)
            .SetTag(SpanAttrs.ToolId, request.ToolId)
            .SetTag(SpanAttrs.Simulated, true);

        var stepResult = _store.AddStepResult(
            request.RunId,
            $"sim_tool_{request.ToolId}",
            "agent_tool_manager",
            request.Status,
            $"[SIMULATED] {request.Summary ?? request.Status}",
            ["simulated", request.ToolId]);

        _events.Publish(_store.AppendNewEvent("simulation.tool_result.injected", null, new JsonObject
        {
            ["run_id"] = request.RunId,
            ["tool_id"] = request.ToolId,
            ["step_result_id"] = stepResult.Id,
            ["simulated"] = true
        }, ["simulation", "tool.execution"]));

        return new SimulateMessageResponse
        {
            SimulationId = Guid.NewGuid().ToString(),
            TraceId = Activity.Current?.TraceId.ToString() ?? "",
            Simulated = true
        };
    }

    public SimulateMessageResponse ForceApprovalDecision(ForceApprovalDecisionRequest request)
    {
        using var activity = TinadecActivitySource.Instance.StartActivity(SpanNames.AgentApproval);
        activity?
            .SetTag(SpanAttrs.ApprovalId, request.ApprovalId)
            .SetTag(SpanAttrs.ApprovalDecision, request.Decision)
            .SetTag(SpanAttrs.Simulated, true);

        var approval = _store.DecideApproval(request.ApprovalId, request.Decision);

        _events.Publish(_store.AppendNewEvent($"simulation.approval.{request.Decision}", approval?.SessionId,
            new JsonObject
            {
                ["approval_id"] = request.ApprovalId,
                ["decision"] = request.Decision,
                ["simulated"] = true
            }, ["simulation", "approval.decide"]));

        return new SimulateMessageResponse
        {
            SimulationId = Guid.NewGuid().ToString(),
            TraceId = Activity.Current?.TraceId.ToString() ?? "",
            Simulated = true
        };
    }

    public SimulateMessageResponse PatchAgentState(PatchAgentStateRequest request)
    {
        using var activity = TinadecActivitySource.Instance.StartActivity("agent.state_patch");
        activity?
            .SetTag(SpanAttrs.SessionId, request.SessionId)
            .SetTag(SpanAttrs.AgentId, request.AgentId)
            .SetTag(SpanAttrs.Simulated, true);

        // Log the state patch as an event
        var stateJson = new JsonObject();
        foreach (var kvp in request.State)
            stateJson[kvp.Key] = kvp.Value is not null ? JsonValue.Create(kvp.Value) : null;

        _events.Publish(_store.AppendNewEvent("simulation.state.patched", request.SessionId, new JsonObject
        {
            ["agent_id"] = request.AgentId,
            ["patch"] = stateJson,
            ["simulated"] = true
        }, ["simulation", "agent.state"]));

        return new SimulateMessageResponse
        {
            SimulationId = Guid.NewGuid().ToString(),
            TraceId = Activity.Current?.TraceId.ToString() ?? "",
            Simulated = true
        };
    }
}
