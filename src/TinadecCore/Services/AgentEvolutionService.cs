using System.Text.Json;
using System.Text.Json.Nodes;
using Tinadec.Contracts.Events;
using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Storage;
using TinadecModel.Abstractions;

namespace TinadecCore.Services;

/// <summary>
/// 启发式 Agent 进化服务。观察工作流模式，生成 AgentCandidate 提案，
/// 支持审批后提升为正式 AgentProfile。让双层智能体架构能够自演化。
/// </summary>
public sealed class AgentEvolutionService(
    CoreStore store,
    IToolRegistry toolRegistry,
    EventHub events,
    IModelInvocationRuntime? modelRuntime = null)
{
    private const string EvolverAgentId = "agent_evolver";

    /// <summary>
    /// 分析近期工作流事件，识别重复模式，生成 Agent 候选提案。
    /// </summary>
    public async Task<IReadOnlyList<AgentEvolutionProposalDto>> GenerateProposalsAsync(
        string? sessionId = null,
        int lookbackEventCount = 200,
        CancellationToken cancellationToken = default)
    {
        var recentEvents = store.ListEvents(sessionId)
            .TakeLast(lookbackEventCount)
            .ToList();

        var patterns = ExtractPatterns(recentEvents);
        var existingCandidates = store.ListAgentCandidates();
        var existingNames = existingCandidates.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var proposals = new List<AgentEvolutionProposalDto>();

        foreach (var pattern in patterns)
        {
            if (existingNames.Contains(pattern.Name))
                continue;

            var proposal = new AgentEvolutionProposalDto(
                $"evo_{Guid.NewGuid():N}",
                EvolverAgentId,
                pattern.Name,
                pattern.Layer,
                pattern.AgentType,
                pattern.Description,
                pattern.SuggestedTools,
                pattern.EvaluationNotes,
                pattern.ObservedPatterns,
                pattern.ConfidenceScore,
                "proposed",
                DateTimeOffset.UtcNow);

            proposals.Add(proposal);

            // 持久化为 AgentCandidate
            store.AddAgentCandidate(new AgentCandidateSeed(
                proposal.Id,
                EvolverAgentId,
                proposal.Name,
                proposal.Layer,
                proposal.AgentType,
                proposal.Description,
                proposal.SuggestedTools,
                proposal.EvaluationNotes));

            PublishProposalEvent("agent.evolution.proposal_generated", proposal);
        }

        // 如果有模型运行时，可以让 Evolver Agent 做二次评估
        if (modelRuntime is not null && proposals.Count > 0)
            proposals = await EvaluateProposalsWithModelAsync(proposals, cancellationToken);

        return proposals;
    }

    /// <summary>
    /// 列出所有进化提案（候选 Agent）。
    /// </summary>
    public IReadOnlyList<AgentEvolutionProposalDto> ListProposals()
    {
        return store.ListAgentCandidates()
            .Select(c => new AgentEvolutionProposalDto(
                c.Id,
                c.GeneratedByAgentId,
                c.Name,
                c.Layer,
                c.AgentType,
                c.Description,
                c.SuggestedTools,
                c.EvaluationNotes,
                Array.Empty<string>(),
                0.5,
                c.Status,
                c.CreatedAt))
            .ToList();
    }

    /// <summary>
    /// 将候选 Agent 提升为正式 AgentProfile。需要用户审批。
    /// </summary>
    public AgentProfileDto? PromoteCandidate(
        string candidateId,
        PromoteAgentCandidateRequest request)
    {
        var candidate = store.ListAgentCandidates()
            .FirstOrDefault(c => c.Id.Equals(candidateId, StringComparison.OrdinalIgnoreCase));
        if (candidate is null) return null;

        var agentId = $"agent_{request.AgentId}";
        var profile = new AgentProfileDto(
            agentId,
            candidate.Name,
            candidate.Layer,
            candidate.AgentType,
            request.Mode,
            candidate.Description,
            request.ModelRoutePurpose,
            request.AllowedTools,
            request.Capabilities,
            request.SystemPrompt,
            true, // enabled
            false, // is_built_in
            DateTimeOffset.UtcNow);

        store.SaveAgentProfile(agentId, new SaveAgentProfileRequest(
            profile.Name,
            profile.Layer,
            profile.AgentType,
            profile.Mode,
            profile.Description,
            profile.ModelRoutePurpose,
            profile.AllowedTools,
            profile.Capabilities,
            profile.SystemPrompt,
            profile.Enabled));

        store.UpdateCandidateStatus(candidateId, "promoted");

        PublishProposalEvent("agent.evolution.promoted", new AgentEvolutionProposalDto(
            candidateId, candidate.GeneratedByAgentId, candidate.Name,
            candidate.Layer, candidate.AgentType, candidate.Description,
            candidate.SuggestedTools, candidate.EvaluationNotes,
            Array.Empty<string>(), 1.0, "promoted", DateTimeOffset.UtcNow));

        return profile;
    }

    /// <summary>
    /// 拒绝候选 Agent 提案。
    /// </summary>
    public bool RejectProposal(string candidateId, string? reason = null)
    {
        var updated = store.UpdateCandidateStatus(candidateId, "rejected");
        if (!updated) return false;

        PublishProposalEvent("agent.evolution.rejected", new AgentEvolutionProposalDto(
            candidateId, EvolverAgentId, "", "", "", "",
            Array.Empty<string>(), reason is null ? Array.Empty<string>() : [reason],
            Array.Empty<string>(), 0.0, "rejected", DateTimeOffset.UtcNow));
        return true;
    }

    private List<WorkflowPattern> ExtractPatterns(IReadOnlyList<EventEnvelope> events)
    {
        var patterns = new List<WorkflowPattern>();
        var toolUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var toolSequence = new List<string>();

        foreach (var evt in events)
            if (evt.Type.Contains("tool.execution", StringComparison.OrdinalIgnoreCase))
            {
                var toolId = evt.Payload is not null && evt.Payload.TryGetPropertyValue("tool_id", out var tid)
                    ? tid?.ToString() ?? ""
                    : "";
                if (!string.IsNullOrWhiteSpace(toolId))
                {
                    toolUsage[toolId] = toolUsage.GetValueOrDefault(toolId) + 1;
                    toolSequence.Add(toolId);
                }
            }

        // 模式 1：频繁使用的工具组合 → 建议专用 executor
        var topTools = toolUsage.OrderByDescending(kvp => kvp.Value).Take(3).ToList();
        if (topTools.Count >= 2 && topTools.All(t => t.Value >= 3))
        {
            var toolNames = topTools.Select(t => t.Key).ToList();
            patterns.Add(new WorkflowPattern(
                $"{string.Join("-", toolNames.Take(2))}-specialist",
                "execution",
                "specialist",
                $"Observed frequent co-usage of {string.Join(", ", toolNames)}. Consider a dedicated executor agent.",
                toolNames.ToArray(),
                new[]
                {
                    $"Co-used {topTools[0].Value}+ times", "Read-only by default",
                    "Needs evaluation before write access"
                },
                toolSequence.Take(20).Select(t => $"tool:{t}").ToArray(),
                Math.Min(0.9, topTools[0].Value / 10.0)));
        }

        // 模式 2：长会话上下文压缩 → 建议上下文优化 agent
        var contextEvents = events.Count(e => e.Type.Contains("context", StringComparison.OrdinalIgnoreCase));
        if (contextEvents >= 5)
            patterns.Add(new WorkflowPattern(
                "context-optimizer",
                "planning",
                "context-compressor",
                "Detected frequent context pack operations. A specialized context optimizer could improve token efficiency.",
                new[] { "message.read", "event.read" },
                new[] { "Context events detected", "Token budget optimization candidate" },
                new[] { $"context_events:{contextEvents}" },
                Math.Min(0.8, contextEvents / 20.0)));

        // 模式 3：审批密集 → 建议 supervisor 辅助 agent
        var approvalEvents = events.Count(e => e.Type.Contains("approval", StringComparison.OrdinalIgnoreCase));
        if (approvalEvents >= 3)
            patterns.Add(new WorkflowPattern(
                "approval-assistant",
                "planning",
                "supervisor",
                "High approval frequency detected. An approval assistant could pre-screen and recommend decisions.",
                new[] { "approval", "event.read" },
                new[] { "Approval workflow optimization", "Needs safety review before enablement" },
                new[] { $"approval_events:{approvalEvents}" },
                Math.Min(0.7, approvalEvents / 15.0)));

        return patterns;
    }

    private async Task<List<AgentEvolutionProposalDto>> EvaluateProposalsWithModelAsync(
        List<AgentEvolutionProposalDto> proposals,
        CancellationToken cancellationToken)
    {
        try
        {
            var prompt = BuildEvaluationPrompt(proposals);
            var message = new MessageDto($"evo_eval_{Guid.NewGuid():N}", "", "user", prompt, DateTimeOffset.UtcNow);
            var result = await modelRuntime!.InvokeAsync("", "evolution", [message], cancellationToken);

            if (string.Equals(result.Status, "executed", StringComparison.OrdinalIgnoreCase))
            {
                // 简单解析：如果模型认为某个提案不合适，则移除
                var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var proposal in proposals)
                    if (result.Content.Contains(proposal.Name, StringComparison.OrdinalIgnoreCase)
                        && result.Content.Contains("reject", StringComparison.OrdinalIgnoreCase))
                        rejected.Add(proposal.Name);

                if (rejected.Count > 0) proposals = proposals.Where(p => !rejected.Contains(p.Name)).ToList();
            }
        }
        catch
        {
            // 模型评估失败不影响提案生成
        }

        return proposals;
    }

    private static string BuildEvaluationPrompt(List<AgentEvolutionProposalDto> proposals)
    {
        var lines = proposals.Select(p =>
            $"- {p.Name} ({p.Layer}/{p.AgentType}): {p.Description}. Tools: {string.Join(", ", p.SuggestedTools)}. Confidence: {p.ConfidenceScore:F2}");
        return $"""
                You are TinadecOffice's Evolution Agent. Review these agent proposals and identify any that should be rejected.
                Return one line per proposal: "ACCEPT <name>" or "REJECT <name> <reason>".

                Proposals:
                {string.Join("\n", lines)}
                """;
    }

    private void PublishProposalEvent(string type, AgentEvolutionProposalDto proposal)
    {
        var envelope = store.AppendNewEvent(type, null, new JsonObject
        {
            ["proposal_id"] = proposal.Id,
            ["name"] = proposal.Name,
            ["layer"] = proposal.Layer,
            ["agent_type"] = proposal.AgentType,
            ["status"] = proposal.Status,
            ["confidence"] = proposal.ConfidenceScore
        }, ["agent.evolution"]);
        events.Publish(envelope);
    }

    private sealed record WorkflowPattern(
        string Name,
        string Layer,
        string AgentType,
        string Description,
        string[] SuggestedTools,
        string[] EvaluationNotes,
        string[] ObservedPatterns,
        double ConfidenceScore);
}
