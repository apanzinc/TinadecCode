using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Services;
using TinadecCore.Storage;

namespace TinadecCore.Tests;

public sealed class PromptContextServiceTests
{
    [Fact]
    public async Task PreviewForMeetingAgentIncludesDefaultFragmentsAndContextPackSummary()
    {
        var store = CreateStore();
        var project = store.CreateProject("TinadecOffice", Environment.CurrentDirectory);
        var session = store.CreateSession(project.Id, "Prompt context");
        var message = store.AddMessage(session.Id, "user", "Plan a small change.");
        var snapshot = store.CreateOrchestrationRun(session.Id, message.Id, message.Content);
        var planner = new RecordingPromptContextPlannerRuntime();
        var service = new PromptContextService(store, new ToolRegistryService(), planner);

        var preview = await service.BuildForRunAsync(snapshot, userContent: message.Content);

        Assert.Equal("agent_meeting", preview.AgentId);
        Assert.Contains(preview.Fragments, fragment => fragment.Id == "prompt_builtin_meeting_default");
        Assert.Contains(preview.Fragments, fragment => fragment.Id == "prompt_builtin_tool_approval_boundaries");
        Assert.Contains(preview.Fragments, fragment => fragment.Id == "prompt_builtin_context_pack_rules");
        Assert.Contains(snapshot.ContextPacks[0].Id, preview.ContextPackIds);
        Assert.Contains(snapshot.ContextPacks[0].Summary, preview.SystemPrompt);
        Assert.True(preview.EstimatedTokens > 0);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task DisabledFragmentDoesNotParticipateInPreview()
    {
        var store = CreateStore();
        var disabled = store.CreatePromptFragment(new SavePromptFragmentRequest(
            "custom.disabled.secret",
            "Disabled Secret",
            "agent",
            "agent_meeting",
            "custom",
            "disabled fragment content should not appear",
            2000,
            false));
        var service =
            new PromptContextService(store, new ToolRegistryService(), new RecordingPromptContextPlannerRuntime());

        var preview = await service.PreviewAsync(new PromptContextPreviewRequest(
            "agent_meeting",
            "plan-first",
            null,
            null,
            "simple preview"));

        Assert.DoesNotContain(preview.Fragments, fragment => fragment.Id == disabled.Id);
        Assert.DoesNotContain("disabled fragment content should not appear", preview.SystemPrompt);
    }

    [Fact]
    public async Task CustomFragmentWithHigherPriorityIsSelectedAheadOfBuiltInFragments()
    {
        var store = CreateStore();
        var custom = store.CreatePromptFragment(new SavePromptFragmentRequest(
            "custom.meeting.priority",
            "Priority Custom",
            "agent",
            "agent_meeting",
            "custom",
            "custom high priority instruction",
            1200,
            true));
        var service =
            new PromptContextService(store, new ToolRegistryService(), new RecordingPromptContextPlannerRuntime());

        var preview = await service.PreviewAsync(new PromptContextPreviewRequest(
            "agent_meeting",
            "plan-first",
            null,
            null,
            "simple preview"));

        Assert.Equal(custom.Id, preview.Fragments[0].Id);
        Assert.Contains("custom high priority instruction", preview.SystemPrompt);
    }

    [Fact]
    public async Task ComplexTaskTriggersPromptContextEngineerPlan()
    {
        var store = CreateStore();
        var project = store.CreateProject("TinadecOffice", Environment.CurrentDirectory);
        var session = store.CreateSession(project.Id, "Prompt context");
        var userContent = "Create a long-term multi-stage implementation roadmap.";
        var message = store.AddMessage(session.Id, "user", userContent);
        var snapshot = store.CreateOrchestrationRun(session.Id, message.Id, message.Content);
        var planner = new RecordingPromptContextPlannerRuntime { ReturnPlan = true };
        var service = new PromptContextService(store, new ToolRegistryService(), planner);

        var preview = await service.BuildForRunAsync(snapshot, userContent: userContent);

        Assert.Equal(1, planner.CallCount);
        Assert.Contains("model-assisted", preview.SystemPrompt);
        Assert.Empty(preview.Warnings);
    }

    private static CoreStore CreateStore()
    {
        var store = new CoreStore(Path.Combine(Path.GetTempPath(), $"tinadec-prompt-context-{Guid.NewGuid():N}.db"));
        store.Initialize();
        return store;
    }

    private sealed class RecordingPromptContextPlannerRuntime : IPromptContextPlannerRuntime
    {
        public int CallCount { get; private set; }
        public bool ReturnPlan { get; init; }

        public Task<PromptContextPlanDto?> TryCreatePlanAsync(
            PromptContextPlanningInput input,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (!ReturnPlan || string.IsNullOrWhiteSpace(input.RunId))
                return Task.FromResult<PromptContextPlanDto?>(null);

            return Task.FromResult<PromptContextPlanDto?>(new PromptContextPlanDto(
                input.RunId,
                input.AgentId,
                "model-assisted",
                input.CandidateFragments.Select(fragment => fragment.Id).ToArray(),
                "Selected deterministic fragments with model assistance.",
                "agent_prompt_context_engineer"));
        }
    }
}
