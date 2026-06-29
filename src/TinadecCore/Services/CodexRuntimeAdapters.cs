using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Storage;

namespace TinadecCore.Services;

public sealed class CodexRuntimeKernelAdapter : IRuntimeKernelAdapter
{
    public string Id => "codex-rust";
    public string DisplayName => "Codex Rust Kernel";

    public IReadOnlyList<string> Capabilities { get; } =
    [
        "file.search",
        "file.glob",
        "file.read",
        "directory.list",
        "file.grep",
        "patch.apply",
        "shell.approved",
        "review.format"
    ];
}

public sealed class CodexToolInvocationAdapter(ICodeToolClient codeToolClient) : IToolInvocationAdapter
{
    public string Id => "codex-rust";

    public bool CanInvoke(ToolDescriptorDto tool)
    {
        return string.Equals(tool.Source, Id, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tool.Source, "code", StringComparison.OrdinalIgnoreCase);
    }

    public Task<CodeToolExecuteResultDto> InvokeAsync(
        ToolDescriptorDto tool,
        CodeToolExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        return codeToolClient.ExecuteAsync(tool, request, cancellationToken);
    }
}

public sealed class CoreToolInvocationAdapter(PromptContextService promptContextService) : IToolInvocationAdapter
{
    public string Id => "core";

    public bool CanInvoke(ToolDescriptorDto tool)
    {
        return string.Equals(tool.Source, Id, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CodeToolExecuteResultDto> InvokeAsync(
        ToolDescriptorDto tool,
        CodeToolExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(tool.Id, "prompt_context_resolve", StringComparison.OrdinalIgnoreCase))
            return new CodeToolExecuteResultDto(
                tool.Id,
                "failed",
                $"Core tool '{tool.Id}' is not supported by this adapter.",
                ["core.adapter.unsupported"],
                new Dictionary<string, object?>(),
                false,
                null);

        var args = request.Arguments ?? new Dictionary<string, object?>();
        var preview = await promptContextService.PreviewAsync(
            new PromptContextPreviewRequest(
                ReadString(args, "agent_id") ?? ReadString(args, "agentId") ?? "agent_meeting",
                ReadString(args, "mode"),
                request.SessionId ?? ReadString(args, "session_id") ?? ReadString(args, "sessionId"),
                request.RunId ?? ReadString(args, "run_id") ?? ReadString(args, "runId"),
                ReadString(args, "user_content") ?? ReadString(args, "userContent")),
            cancellationToken: cancellationToken);

        var data = new Dictionary<string, object?>
        {
            ["agent_id"] = preview.AgentId,
            ["mode"] = preview.Mode,
            ["fragment_ids"] = preview.Fragments.Select(fragment => fragment.Id).ToArray(),
            ["fragments"] = preview.Fragments.Select(fragment => new Dictionary<string, object?>
            {
                ["id"] = fragment.Id,
                ["key"] = fragment.Key,
                ["title"] = fragment.Title,
                ["scope"] = fragment.Scope,
                ["category"] = fragment.Category,
                ["priority"] = fragment.Priority,
                ["is_builtin"] = fragment.IsBuiltIn
            }).ToArray(),
            ["context_pack_ids"] = preview.ContextPackIds,
            ["estimated_tokens"] = preview.EstimatedTokens,
            ["warning_count"] = preview.Warnings.Count,
            ["warnings"] = preview.Warnings
        };

        return new CodeToolExecuteResultDto(
            tool.Id,
            "completed",
            $"Resolved {preview.Fragments.Count} prompt fragments for {preview.AgentId}.",
            preview.Fragments.Select(fragment => $"prompt_fragment:{fragment.Id}").ToArray(),
            data,
            false,
            null);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null) return null;

        return value switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text.Trim(),
            System.Text.Json.JsonElement element when element.ValueKind == System.Text.Json.JsonValueKind.String =>
                element.GetString()?.Trim(),
            _ => value.ToString()?.Trim()
        };
    }
}
