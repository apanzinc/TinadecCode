using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinadecCore.Storage;
using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Tracing;

namespace TinadecCore.Services;

public sealed class ToolExecutionService(
    CoreStore store,
    EventHub events,
    IToolRegistry tools,
    ICapabilityPolicy capabilityPolicy,
    IEnumerable<IToolInvocationAdapter> invocationAdapters)
{
    public async Task<ToolExecutionResponseDto?> ExecuteAsync(
        string runId,
        string toolId,
        CodeToolExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        using var activity = TinadecActivitySource.Instance.StartActivity(SpanNames.AgentToolExecution);
        activity?
            .SetTag(SpanAttrs.RunId, runId)
            .SetTag(SpanAttrs.ToolId, toolId);

        var run = store.GetRun(runId);
        var tool = tools.Resolve(toolId);
        if (run is null || tool is null) return null;

        var sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? run.SessionId : request.SessionId;
        var normalizedRequest = request with
        {
            SessionId = sessionId,
            RunId = runId
        };

        Publish("tool.execution.requested", sessionId, new JsonObject
        {
            ["run_id"] = runId,
            ["tool_id"] = tool.Id,
            ["requires_approval"] = tool.RequiresApproval
        }, ["tool.execution"]);

        var approvalRequirement = capabilityPolicy.Evaluate("approval", tool);
        if (tool.RequiresApproval || approvalRequirement.Required)
        {
            activity?.SetTag(SpanAttrs.RequiresApproval, true);
            using var approvalSpan = TinadecActivitySource.Instance.StartActivity(SpanNames.AgentApproval);
            approvalSpan?
                .SetTag(SpanAttrs.ToolId, tool.Id)
                .SetTag(SpanAttrs.ApprovalKind, tool.DisplayName);

            var approval = ResolveApprovedApproval(normalizedRequest.ApprovalId);
            if (approval is null)
            {
                approval = store.CreateApproval(new CreateApprovalRequest(
                    sessionId,
                    tool.Id,
                    tool.DisplayName,
                    SummarizeCommand(tool.Id, normalizedRequest.Arguments),
                    normalizedRequest.Cwd));
                Publish("tool.execution.approval_required", sessionId, new JsonObject
                {
                    ["run_id"] = runId,
                    ["tool_id"] = tool.Id,
                    ["approval_id"] = approval.Id,
                    ["reason"] = approvalRequirement.Reason
                }, ["tool.execution", "approval.ask"]);

                return new ToolExecutionResponseDto("approval_required", tool, approval, null, null);
            }

            normalizedRequest = normalizedRequest with { ApprovalId = approval.Id };
        }

        var result = await InvokeToolAsync(tool, normalizedRequest, cancellationToken);

        var stepResult = store.AddStepResult(
            runId,
            string.IsNullOrWhiteSpace(normalizedRequest.TaskNodeId) ? $"tool_{tool.Id}" : normalizedRequest.TaskNodeId,
            "agent_tool_manager",
            result.Status,
            result.Summary,
            result.Evidence);

        Publish(result.Status is "failed" or "blocked" ? "tool.execution.failed" : "tool.execution.completed",
            sessionId,
            new JsonObject
            {
                ["run_id"] = runId,
                ["tool_id"] = tool.Id,
                ["status"] = result.Status,
                ["step_result_id"] = stepResult.Id,
                ["data"] = JsonSerializer.SerializeToNode(result.Data, TinadecJson.Options)
            },
            ["tool.execution", "step.result"]);

        return new ToolExecutionResponseDto("executed", tool, null, result, stepResult);
    }

    private ApprovalDto? ResolveApprovedApproval(string? approvalId)
    {
        if (string.IsNullOrWhiteSpace(approvalId)) return null;

        var approval = store.GetApproval(approvalId);
        return string.Equals(approval?.Status, "approved", StringComparison.OrdinalIgnoreCase)
            ? approval
            : null;
    }

    private async Task<CodeToolExecuteResultDto> InvokeToolAsync(
        ToolDescriptorDto tool,
        CodeToolExecuteRequest request,
        CancellationToken cancellationToken)
    {
        var adapter = invocationAdapters.FirstOrDefault(item => item.CanInvoke(tool));
        if (adapter is null)
            return new CodeToolExecuteResultDto(
                tool.Id,
                "failed",
                $"No Core invocation adapter is registered for tool source '{tool.Source}'.",
                ["adapter missing", tool.Source],
                new Dictionary<string, object?>(),
                false,
                null);

        try
        {
            return await adapter.InvokeAsync(tool, request, cancellationToken);
        }
        catch (Exception ex)
        {
            return new CodeToolExecuteResultDto(
                tool.Id,
                "failed",
                $"Tool adapter '{adapter.Id}' failed: {ex.Message}",
                ["adapter failed", adapter.Id],
                new Dictionary<string, object?>(),
                false,
                null);
        }
    }

    private static string SummarizeCommand(string toolId, IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return toolId;

        return JsonSerializer.Serialize(new
        {
            tool_id = toolId,
            arguments
        }, TinadecJson.Options);
    }

    private void Publish(string type, string? sessionId, JsonObject payload, IReadOnlyList<string> capabilities)
    {
        var envelope = store.AppendNewEvent(type, sessionId, payload, capabilities);
        events.Publish(envelope);
    }
}
