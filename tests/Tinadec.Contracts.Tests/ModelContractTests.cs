using System.Text.Json;
using System.Text.Json.Nodes;
using Tinadec.Contracts.Models;

namespace Tinadec.Contracts.Tests;

public sealed class ModelContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public void ModelInvocationContractsSerializeWithSnakeCaseFields()
    {
        var request = new ModelInvocationRequestDto(
            [
                new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UnixEpoch)
            ],
            "Be concise.",
            [
                new JsonObject
                {
                    ["name"] = "search",
                    ["description"] = "Search workspace"
                }
            ],
            new ModelSettingsDto("https://example.test", "model-a", true, DateTimeOffset.UnixEpoch),
            new ModelStateHandleDto("state_1", DateTimeOffset.UnixEpoch));

        var response = new ModelInvocationResponseDto(
            "Done",
            new ModelUsageDto(10, 5, 15),
            ModelFinishReason.ToolCalls,
            new ProviderMetadataDto(
                "provider_1",
                "model-a",
                "ExampleProvider",
                new Dictionary<string, object?> { ["request_id"] = "req_1" }),
            new ModelStateHandleDto("state_2", DateTimeOffset.UnixEpoch),
            ProviderErrorCategory.RateLimited,
            "Too many requests");

        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var responseJson = JsonSerializer.Serialize(response, JsonOptions);

        Assert.Contains("\"system_prompt\":\"Be concise.\"", requestJson);
        Assert.Contains("\"state_handle\"", requestJson);
        Assert.Contains("\"text_content\":\"Done\"", responseJson);
        Assert.Contains("\"prompt_tokens\":10", responseJson);
        Assert.Contains("\"completion_tokens\":5", responseJson);
        Assert.Contains("\"total_tokens\":15", responseJson);
        Assert.Contains("\"finish_reason\":\"tool_calls\"", responseJson);
        Assert.Contains("\"provider_id\":\"provider_1\"", responseJson);
        Assert.Contains("\"raw_provider_name\":\"ExampleProvider\"", responseJson);
        Assert.Contains("\"error_category\":\"rate_limited\"", responseJson);
    }

    [Fact]
    public void ProviderErrorCategoriesIncludeRequiredNormalizedValues()
    {
        var values = Enum.GetNames<ProviderErrorCategory>();

        Assert.Equal(
            [
                "AuthenticationFailed",
                "RateLimited",
                "Timeout",
                "ProviderUnavailable",
                "InvalidRequest",
                "Cancelled",
                "Unknown"
            ],
            values);
    }

    [Fact]
    public void ProviderCapabilityMetadataRepresentsRequiredFields()
    {
        var capability = new ProviderCapabilityDto(
            true,
            true,
            true,
            true,
            128000,
            false,
            "api_key",
            ProviderHealthStatus.Cooldown);

        var json = JsonSerializer.Serialize(capability, JsonOptions);

        Assert.Contains("\"supports_streaming\":true", json);
        Assert.Contains("\"supports_tools\":true", json);
        Assert.Contains("\"supports_json_mode\":true", json);
        Assert.Contains("\"supports_system_prompt\":true", json);
        Assert.Contains("\"max_context_tokens\":128000", json);
        Assert.Contains("\"requires_workspace\":false", json);
        Assert.Contains("\"credential_kind\":\"api_key\"", json);
        Assert.Contains("\"health_status\":\"cooldown\"", json);
    }

    [Fact]
    public void HarnessManifestContractSerializesToolAndLayerSummaries()
    {
        var manifest = new HarnessManifestDto(
            "tinadec-core-workflow",
            "Core owns orchestration.",
            new ToolRegistrySummaryDto(
                4,
                3,
                1,
                ["apply_patch"],
                ["core", "code", "codex-rust", "extension"],
                "Core canonicalizes duplicate tool ids by source precedence."),
            [
                new AgentLayerManifestDto(
                    "planning",
                    "Active planning and supervision layer",
                    2,
                    2,
                    1,
                    false,
                    true,
                    ["meeting", "supervisor"],
                    ["prompt_context_resolve"])
            ],
            [
                new ToolProviderManifestDto(
                    "code",
                    "Code Tool Suite",
                    "tool-layer",
                    "active",
                    3,
                    3,
                    0,
                    2,
                    1,
                    ["project", "runtime"])
            ],
            [
                new ToolRiskManifestDto(
                    "workspace-write",
                    1,
                    true,
                    "Requires approval.")
            ],
            [
                new ToolDescriptorDto(
                    "apply_patch",
                    "Apply Patch",
                    "programming",
                    "codex-rust",
                    "workspace-write",
                    true,
                    "/api/v1/code/tools/apply_patch/execute",
                    ["patch.apply"])
            ],
            ["Code is a Tool-layer suite."]);

        var json = JsonSerializer.Serialize(manifest, JsonOptions);

        Assert.Contains("\"ownership_model\":\"Core owns orchestration.\"", json);
        Assert.Contains("\"tool_registry\"", json);
        Assert.Contains("\"declared_tool_count\":4", json);
        Assert.Contains("\"canonical_tool_count\":3", json);
        Assert.Contains("\"duplicate_tool_id_count\":1", json);
        Assert.Contains("\"agent_layers\"", json);
        Assert.Contains("\"enabled_agent_count\":2", json);
        Assert.Contains("\"max_parallel_executors\":1", json);
        Assert.Contains("\"tool_providers\"", json);
        Assert.Contains("\"approval_required_count\":2", json);
        Assert.Contains("\"tool_risks\"", json);
        Assert.Contains("\"requires_human_checkpoint\":true", json);
        Assert.Contains("\"execute_endpoint\":\"/api/v1/code/tools/apply_patch/execute\"", json);
    }

    [Fact]
    public void ToolSearchResultContractSerializesCoreDiscoveryMetadata()
    {
        var result = new ToolSearchResultDto(
            new ToolDescriptorDto(
                "git_worktree_manager",
                "Git Worktree Manager",
                "programming",
                "code",
                "git-write",
                true,
                "/api/v1/code/tools/git_worktree_manager/execute",
                [
                    "git.status", "git.diff", "git.stage", "git.unstage", "git.worktree", "git.commit", "git.push",
                    "workspace.isolation"
                ]),
            420,
            ["id", "capabilities"],
            "tool-layer",
            true,
            "Requires Core approval before dispatch.");

        var json = JsonSerializer.Serialize(result, JsonOptions);

        Assert.Contains("\"score\":420", json);
        Assert.Contains("\"matched_fields\":[\"id\",\"capabilities\"]", json);
        Assert.Contains("\"provider_layer\":\"tool-layer\"", json);
        Assert.Contains("\"requires_human_checkpoint\":true", json);
        Assert.Contains("\"approval_summary\":\"Requires Core approval before dispatch.\"", json);
    }

    [Fact]
    public void ToolExecutionTimelineContractSerializesAuditMetadata()
    {
        var item = new ToolExecutionTimelineItemDto(
            "step_1",
            "run_1",
            "sess_1",
            "read_file",
            "Read File",
            "codex-rust",
            "native-glue",
            "read-only",
            false,
            "completed",
            null,
            "step_1",
            "Read a file.",
            ["file:README.md"],
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(2),
            2000,
            10,
            11,
            ["tool.execution.requested", "tool.execution.completed"],
            "Read-only tool execution was auto-dispatchable under Core policy.");

        var json = JsonSerializer.Serialize(item, JsonOptions);

        Assert.Contains("\"tool_display_name\":\"Read File\"", json);
        Assert.Contains("\"provider_layer\":\"native-glue\"", json);
        Assert.Contains("\"requires_approval\":false", json);
        Assert.Contains("\"step_result_id\":\"step_1\"", json);
        Assert.Contains("\"duration_ms\":2000", json);
        Assert.Contains("\"requested_seq\":10", json);
        Assert.Contains("\"updated_seq\":11", json);
        Assert.Contains("\"event_types\":[\"tool.execution.requested\",\"tool.execution.completed\"]", json);
        Assert.Contains("\"checkpoint_summary\":\"Read-only tool execution was auto-dispatchable under Core policy.\"",
            json);
    }

    [Fact]
    public void RuntimeReadinessReceiptContractSerializesCoreStartupEvidence()
    {
        var receipt = new RuntimeReadinessReceiptDto(
            "warning",
            DateTimeOffset.UnixEpoch,
            "tinadec-core-workflow",
            "readiness_1",
            [
                new RuntimeReadinessComponentDto(
                    "tool_registry",
                    "Tool Registry",
                    "ready",
                    "Core tool registry is canonical.",
                    ["canonical_tool_count:15"])
            ],
            1,
            1,
            0);

        var json = JsonSerializer.Serialize(receipt, JsonOptions);

        Assert.Contains("\"generated_at\":\"1970-01-01T00:00:00+00:00\"", json);
        Assert.Contains("\"receipt_id\":\"readiness_1\"", json);
        Assert.Contains("\"ready_count\":1", json);
        Assert.Contains("\"warning_count\":1", json);
        Assert.Contains("\"blocked_count\":0", json);
        Assert.Contains("\"components\"", json);
        Assert.Contains("\"evidence\":[\"canonical_tool_count:15\"]", json);
    }

    [Fact]
    public void ToolLayerReadinessReceiptContractSerializesToolAndAgentScopeEvidence()
    {
        var receipt = new ToolLayerReadinessReceiptDto(
            "warning",
            DateTimeOffset.UnixEpoch,
            "tinadec-core-workflow",
            "tool_layer_readiness_1",
            1,
            0,
            1,
            0,
            1,
            1,
            0,
            0,
            1,
            1,
            1,
            0,
            [
                new ToolLayerToolReadinessDto(
                    "sandbox_exec",
                    "Sandbox Exec",
                    "codex-rust",
                    "native-glue",
                    "shell",
                    "warning",
                    true,
                    true,
                    true,
                    1,
                    "Tool is declared for future integration.",
                    ["requires_human_checkpoint:True"])
            ],
            [
                new ToolLayerAgentScopeReadinessDto(
                    "executor_git_manager",
                    "Git Manager Subagent",
                    "execution",
                    "git-manager",
                    true,
                    "ready",
                    2,
                    1,
                    1,
                    0,
                    1,
                    ["git_worktree_manager"],
                    [],
                    "Execution agent scope resolves to Core tools.",
                    ["dispatchable_tool_count:1"])
            ],
            ["Core owns Tool-layer readiness."]);

        var json = JsonSerializer.Serialize(receipt, JsonOptions);

        Assert.Contains("\"receipt_id\":\"tool_layer_readiness_1\"", json);
        Assert.Contains("\"tool_count\":1", json);
        Assert.Contains("\"execution_agent_count\":1", json);
        Assert.Contains("\"human_checkpoint_tool_count\":1", json);
        Assert.Contains("\"unresolved_scope_count\":0", json);
        Assert.Contains("\"tool_id\":\"sandbox_exec\"", json);
        Assert.Contains("\"provider_layer\":\"native-glue\"", json);
        Assert.Contains("\"requires_human_checkpoint\":true", json);
        Assert.Contains("\"is_future\":true", json);
        Assert.Contains("\"assigned_execution_agent_count\":1", json);
        Assert.Contains("\"agent_scopes\"", json);
        Assert.Contains("\"dispatchable_tool_count\":1", json);
    }

    [Fact]
    public void ModelReadinessReceiptContractSerializesProviderAndRouteEvidence()
    {
        var receipt = new ModelReadinessReceiptDto(
            "blocked",
            DateTimeOffset.UnixEpoch,
            "model_readiness_1",
            1,
            0,
            0,
            1,
            1,
            0,
            0,
            1,
            [
                new ModelProviderReadinessDto(
                    "openai_default",
                    "OpenAI Compatible",
                    "openai-compatible",
                    "api-key",
                    "blocked",
                    "needs_key",
                    true,
                    false,
                    ["chat"],
                    "Provider status 'needs_key' blocks routed model traffic.",
                    ["provider_status:needs_key"])
            ],
            [
                new ModelRouteReadinessDto(
                    "chat",
                    "openai_default",
                    "OpenAI Compatible",
                    "gpt-5.4-mini",
                    "blocked",
                    "Route provider is needs_key.",
                    ["provider_status:needs_key"])
            ],
            ["Core owns provider readiness."]);

        var json = JsonSerializer.Serialize(receipt, JsonOptions);

        Assert.Contains("\"receipt_id\":\"model_readiness_1\"", json);
        Assert.Contains("\"provider_count\":1", json);
        Assert.Contains("\"blocked_provider_count\":1", json);
        Assert.Contains("\"blocked_route_count\":1", json);
        Assert.Contains("\"provider_instance_id\":\"openai_default\"", json);
        Assert.Contains("\"provider_status\":\"needs_key\"", json);
        Assert.Contains("\"has_credential\":false", json);
        Assert.Contains("\"route_purposes\":[\"chat\"]", json);
    }

    [Fact]
    public void ModelCatalogReadinessReceiptContractSerializesTemplateEvidence()
    {
        var receipt = new ModelCatalogReadinessReceiptDto(
            "warning",
            DateTimeOffset.UnixEpoch,
            "model_catalog_readiness_1",
            1,
            0,
            1,
            0,
            3,
            1,
            1,
            [
                new ModelCatalogTemplateReadinessDto(
                    "openai-compatible",
                    "openai-compatible",
                    "OpenAI Compatible",
                    "http",
                    "api_key",
                    "warning",
                    "openai-compatible",
                    "registered",
                    1,
                    true,
                    "credential_gated_remote_advisory",
                    "Template is available through the Core catalog.",
                    [
                        "runtime_module_status:registered",
                        "live_discovery_policy:credential_gated_remote_advisory"
                    ])
            ],
            ["Core owns model catalog readiness."]);

        var json = JsonSerializer.Serialize(receipt, JsonOptions);

        Assert.Contains("\"receipt_id\":\"model_catalog_readiness_1\"", json);
        Assert.Contains("\"template_count\":1", json);
        Assert.Contains("\"runtime_module_count\":3", json);
        Assert.Contains("\"advisory_probe_template_count\":1", json);
        Assert.Contains("\"runtime_module_family\":\"openai-compatible\"", json);
        Assert.Contains("\"runtime_module_status\":\"registered\"", json);
        Assert.Contains("\"configured_instance_count\":1", json);
        Assert.Contains("\"supports_live_discovery\":true", json);
        Assert.Contains("\"live_discovery_policy\":\"credential_gated_remote_advisory\"", json);
    }

    [Fact]
    public void PromptFragmentContractUsesPlanFieldNames()
    {
        var fragment = new PromptFragmentDto(
            "prompt_1",
            "builtin.meeting.default",
            "Meeting Agent Default",
            "agent",
            "agent_meeting",
            "identity",
            "Be useful.",
            900,
            true,
            true,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(fragment, JsonOptions);

        Assert.Contains("\"target_agent_id\":\"agent_meeting\"", json);
        Assert.Contains("\"is_builtin\":true", json);
        Assert.DoesNotContain("\"is_built_in\"", json);
    }

    [Fact]
    public void ProviderSpecificWireFieldsDoNotLeakToNormalizedDtoTopLevel()
    {
        var response = new ModelInvocationResponseDto(
            "Done",
            new ModelUsageDto(1, 2, 3),
            ModelFinishReason.Stop,
            new ProviderMetadataDto(
                "provider_1",
                "model-a",
                "ExampleProvider",
                new Dictionary<string, object?>
                {
                    ["previous_response_id"] = "resp_1",
                    ["choices"] = 1
                }),
            null,
            null,
            null);

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.DoesNotContain("tool_use", root.Select(property => property.Key));
        Assert.DoesNotContain("previous_response_id", root.Select(property => property.Key));
        Assert.DoesNotContain("choices", root.Select(property => property.Key));
    }
}
