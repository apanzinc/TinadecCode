using System.Text;
using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Storage;
using TinadecModel.Abstractions;

namespace TinadecCore.Services;

public sealed class PromptContextService(
    CoreStore store,
    IToolRegistry toolRegistry,
    IPromptContextPlannerRuntime plannerRuntime)
{
    private const string MeetingAgentId = "agent_meeting";
    private const string PromptContextEngineerAgentId = "agent_prompt_context_engineer";

    public async Task<PromptContextPreviewDto> PreviewAsync(
        PromptContextPreviewRequest request,
        OrchestrationSnapshotDto? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        var agent = ResolveAgent(request.AgentId);
        var agentId = string.IsNullOrWhiteSpace(agent?.Id) ? NormalizeAgentId(request.AgentId) : agent!.Id;
        var mode = ResolveMode(request.Mode, agent);
        var resolvedSnapshot = ResolveSnapshot(request, snapshot);
        var sessionId = request.SessionId ?? resolvedSnapshot?.Run?.SessionId;
        var runId = request.RunId ?? resolvedSnapshot?.Run?.Id;
        var userContent = ResolveUserContent(request.UserContent, sessionId, resolvedSnapshot);
        var candidateFragments = SelectFragments(agentId, mode, sessionId);
        var contextPacks = resolvedSnapshot?.ContextPacks ?? [];
        var taskNodes = resolvedSnapshot?.Nodes ?? [];
        var tools = toolRegistry.ListTools();
        var warnings = new List<string>();
        var complex = IsComplex(userContent, taskNodes, resolvedSnapshot?.Assignments ?? [], contextPacks, tools);

        PromptContextPlanDto? plan = null;
        if (complex && !string.IsNullOrWhiteSpace(runId))
        {
            try
            {
                plan = await plannerRuntime.TryCreatePlanAsync(
                    new PromptContextPlanningInput(
                        sessionId ?? "",
                        runId,
                        agentId,
                        mode,
                        candidateFragments,
                        contextPacks,
                        taskNodes,
                        tools,
                        userContent,
                        true),
                    cancellationToken);

                if (plan is not null)
                {
                    store.SavePromptContextPlan(plan);
                }
                else
                {
                    warnings.Add("Prompt Context Engineer was unavailable; deterministic prompt assembly was used.");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                warnings.Add($"Prompt Context Engineer plan failed; deterministic prompt assembly was used. {ex.Message}");
            }
        }

        var selectedFragments = ApplyPlan(candidateFragments, plan);
        if (selectedFragments.Count == 0)
        {
            warnings.Add("No enabled prompt fragments matched the target agent.");
        }

        var systemPrompt = BuildSystemPrompt(agentId, mode, selectedFragments, contextPacks, taskNodes, tools, plan, warnings);
        return new PromptContextPreviewDto(
            agentId,
            mode,
            selectedFragments,
            contextPacks.Select(pack => pack.Id).ToArray(),
            EstimateTokens(systemPrompt),
            systemPrompt,
            warnings);
    }

    public async Task<PromptContextPreviewDto> BuildForRunAsync(
        OrchestrationSnapshotDto snapshot,
        string agentId = MeetingAgentId,
        string? userContent = null,
        CancellationToken cancellationToken = default)
    {
        return await PreviewAsync(
            new PromptContextPreviewRequest(
                agentId,
                snapshot.Run is null ? null : ResolveAgent(agentId)?.Mode,
                snapshot.Run?.SessionId,
                snapshot.Run?.Id,
                userContent),
            snapshot,
            cancellationToken);
    }

    private AgentProfileDto? ResolveAgent(string agentId)
    {
        var normalized = NormalizeAgentId(agentId);
        return store.ListAgentProfiles()
            .FirstOrDefault(agent => agent.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private OrchestrationSnapshotDto? ResolveSnapshot(
        PromptContextPreviewRequest request,
        OrchestrationSnapshotDto? snapshot)
    {
        if (snapshot is not null)
        {
            return snapshot;
        }

        if (!string.IsNullOrWhiteSpace(request.RunId))
        {
            return store.GetOrchestrationSnapshotByRun(request.RunId);
        }

        return string.IsNullOrWhiteSpace(request.SessionId)
            ? null
            : store.GetOrchestrationSnapshot(request.SessionId);
    }

    private string ResolveUserContent(
        string? requestUserContent,
        string? sessionId,
        OrchestrationSnapshotDto? snapshot)
    {
        if (!string.IsNullOrWhiteSpace(requestUserContent))
        {
            return requestUserContent.Trim();
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var latestUserMessage = store.ListMessages(sessionId)
                .Where(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(message => message.CreatedAt)
                .FirstOrDefault();
            if (latestUserMessage is not null)
            {
                return latestUserMessage.Content;
            }
        }

        return snapshot?.Run?.Summary ?? "";
    }

    private IReadOnlyList<PromptFragmentDto> SelectFragments(
        string agentId,
        string mode,
        string? sessionId)
    {
        return store.ListPromptFragments(enabled: true)
            .Where(fragment => AppliesTo(fragment, agentId, mode, sessionId))
            .GroupBy(fragment => fragment.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(fragment => fragment.Priority)
                .ThenBy(fragment => fragment.IsBuiltIn)
                .First())
            .OrderByDescending(fragment => fragment.Priority)
            .ThenBy(fragment => fragment.IsBuiltIn)
            .ThenBy(fragment => fragment.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool AppliesTo(PromptFragmentDto fragment, string agentId, string mode, string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(fragment.TargetAgentId) &&
            !fragment.TargetAgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fragment.Scope switch
        {
            "global" => true,
            "agent" => string.IsNullOrWhiteSpace(fragment.TargetAgentId) ||
                fragment.TargetAgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase),
            "mode" => string.IsNullOrWhiteSpace(fragment.TargetAgentId) ||
                fragment.TargetAgentId.Equals(mode, StringComparison.OrdinalIgnoreCase),
            "session" => string.IsNullOrWhiteSpace(fragment.TargetAgentId) ||
                (!string.IsNullOrWhiteSpace(sessionId) && fragment.TargetAgentId.Equals(sessionId, StringComparison.OrdinalIgnoreCase)),
            "project" => true,
            _ => false
        };
    }

    private static IReadOnlyList<PromptFragmentDto> ApplyPlan(
        IReadOnlyList<PromptFragmentDto> candidateFragments,
        PromptContextPlanDto? plan)
    {
        if (plan is null || plan.SelectedFragmentIds.Count == 0)
        {
            return candidateFragments;
        }

        var selected = plan.SelectedFragmentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planned = candidateFragments.Where(fragment => selected.Contains(fragment.Id)).ToArray();
        return planned.Length == 0 ? candidateFragments : planned;
    }

    private static string BuildSystemPrompt(
        string agentId,
        string mode,
        IReadOnlyList<PromptFragmentDto> fragments,
        IReadOnlyList<ContextPackDto> contextPacks,
        IReadOnlyList<TaskNodeDto> taskNodes,
        IReadOnlyList<ToolDescriptorDto> tools,
        PromptContextPlanDto? plan,
        IReadOnlyList<string> warnings)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"TinadecOffice prompt context for {agentId}.");
        builder.AppendLine($"Active agent mode: {mode}.");
        builder.AppendLine();

        foreach (var fragment in fragments)
        {
            builder.AppendLine($"[{fragment.Category}:{fragment.Key}]");
            builder.AppendLine(fragment.Content.Trim());
            builder.AppendLine();
        }

        if (contextPacks.Count > 0)
        {
            builder.AppendLine("[context:packs]");
            foreach (var pack in contextPacks.OrderByDescending(pack => pack.CreatedAt))
            {
                builder.AppendLine($"- {pack.Id}: {pack.Summary} Token budget: {pack.TokenBudget}. Evidence: {string.Join(", ", pack.EvidenceMap.Take(8))}");
            }
            builder.AppendLine();
        }

        if (taskNodes.Count > 0)
        {
            builder.AppendLine("[task_graph:current]");
            foreach (var node in taskNodes.OrderBy(node => node.Priority))
            {
                builder.AppendLine($"- {node.Id}: {node.Title}. Risk: {node.Risk}. Criteria: {string.Join("; ", node.SuccessCriteria)}");
            }
            builder.AppendLine();
        }

        var riskyTools = tools
            .Where(tool => tool.RequiresApproval || tool.Risk is "workspace-write" or "shell" or "git-write")
            .Select(tool => $"{tool.Id}:{tool.Risk}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        if (riskyTools.Length > 0)
        {
            builder.AppendLine("[tools:risk_boundaries]");
            builder.AppendLine($"Approval-gated tools in scope: {string.Join(", ", riskyTools)}.");
            builder.AppendLine();
        }

        if (plan is not null)
        {
            builder.AppendLine("[prompt_context_plan]");
            builder.AppendLine($"Strategy: {plan.Strategy}. Summary: {plan.Summary}");
            builder.AppendLine();
        }

        if (warnings.Count > 0)
        {
            builder.AppendLine("[prompt_context:warnings]");
            foreach (var warning in warnings)
            {
                builder.AppendLine($"- {warning}");
            }
            builder.AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static bool IsComplex(
        string userContent,
        IReadOnlyList<TaskNodeDto> taskNodes,
        IReadOnlyList<AgentAssignmentDto> assignments,
        IReadOnlyList<ContextPackDto> contextPacks,
        IReadOnlyList<ToolDescriptorDto> tools)
    {
        return userContent.Length > 1500
            || taskNodes.Count > 5
            || contextPacks.Count > 2
            || ContainsLongTermPlanningIntent(userContent)
            || taskNodes.Any(node => IsRisky(node.Risk))
            || assignments.Any(assignment => IsRisky(assignment.PermissionMode));
    }

    private static bool ContainsLongTermPlanningIntent(string userContent)
    {
        var value = userContent.ToLowerInvariant();
        return value.Contains("long-term", StringComparison.Ordinal)
            || value.Contains("multi-stage", StringComparison.Ordinal)
            || value.Contains("multi phase", StringComparison.Ordinal)
            || value.Contains("长期", StringComparison.Ordinal)
            || value.Contains("多阶段", StringComparison.Ordinal)
            || value.Contains("分阶段", StringComparison.Ordinal);
    }

    private static bool IsRisky(string value)
    {
        return value.Contains("workspace-write", StringComparison.OrdinalIgnoreCase)
            || value.Contains("shell", StringComparison.OrdinalIgnoreCase)
            || value.Contains("git-write", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveMode(string? requestedMode, AgentProfileDto? agent)
    {
        if (!string.IsNullOrWhiteSpace(requestedMode))
        {
            return requestedMode.Trim();
        }

        return string.IsNullOrWhiteSpace(agent?.Mode) ? "balanced" : agent.Mode;
    }

    private static string NormalizeAgentId(string agentId)
    {
        return string.IsNullOrWhiteSpace(agentId) ? MeetingAgentId : agentId.Trim();
    }

    private static int EstimateTokens(string content)
    {
        return Math.Max(1, (int)Math.Ceiling(content.Length / 4.0));
    }
}

public sealed class ModelPromptContextPlannerRuntime(IModelInvocationRuntime modelRuntime) : IPromptContextPlannerRuntime
{
    public async Task<PromptContextPlanDto?> TryCreatePlanAsync(
        PromptContextPlanningInput input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.SessionId) || string.IsNullOrWhiteSpace(input.RunId))
        {
            return null;
        }

        var request = BuildPlanningRequest(input);
        var message = new MessageDto(
            $"prompt_ctx_req_{Guid.NewGuid():N}",
            input.SessionId,
            "user",
            request,
            DateTimeOffset.UtcNow);

        var result = await modelRuntime.InvokeAsync(
            input.SessionId,
            "context",
            [message],
            cancellationToken,
            BuildPlannerSystemPrompt(input));

        if (!string.Equals(result.Status, "executed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new PromptContextPlanDto(
            input.RunId,
            input.AgentId,
            "model-assisted",
            input.CandidateFragments.Select(fragment => fragment.Id).ToArray(),
            SummarizePlanText(result.Content),
            "agent_prompt_context_engineer");
    }

    private static string BuildPlannerSystemPrompt(PromptContextPlanningInput input)
    {
        return $"""
            You are TinadecOffice's Prompt Context Engineer Agent.
            Select and rank prompt fragments for {input.AgentId} without inventing hidden policy.
            Return a concise optimization summary. Do not include the final system prompt.
            """;
    }

    private static string BuildPlanningRequest(PromptContextPlanningInput input)
    {
        var fragmentLines = input.CandidateFragments
            .Select(fragment => $"- {fragment.Id}: {fragment.Key}, category={fragment.Category}, priority={fragment.Priority}")
            .ToArray();
        var contextLines = input.ContextPacks
            .Select(pack => $"- {pack.Id}: {pack.Summary}")
            .ToArray();
        var nodeLines = input.TaskNodes
            .Select(node => $"- {node.Id}: {node.Title}, risk={node.Risk}")
            .ToArray();

        return $"""
            Agent: {input.AgentId}
            Mode: {input.Mode}
            Complex: {input.IsComplex}
            User content:
            {input.UserContent}

            Candidate fragments:
            {string.Join("\n", fragmentLines)}

            Context packs:
            {string.Join("\n", contextLines)}

            Task nodes:
            {string.Join("\n", nodeLines)}
            """;
    }

    private static string SummarizePlanText(string content)
    {
        var text = string.IsNullOrWhiteSpace(content)
            ? "Prompt Context Engineer returned an empty plan summary."
            : content.Trim().ReplaceLineEndings(" ");
        return text.Length <= 500 ? text : $"{text[..497]}...";
    }
}
