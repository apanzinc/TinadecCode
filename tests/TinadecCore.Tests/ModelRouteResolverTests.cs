using System.Reflection;
using Tinadec.Contracts.Models;
using TinadecModel.Services;
using TinadecModel.Storage;
using TinadecCore.Services;
using TinadecCore.Storage;

namespace TinadecCore.Tests;

public sealed class ModelRouteResolverTests
{
    private const string ChatPurpose = "chat";
    private const string OpenAiPrimaryProviderId = "prov_openai_primary";
    private const string AnthropicBackupProviderId = "prov_anthropic_backup";
    private const string LocalHttpProviderId = "prov_local_http";
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-01-15T12:00:00Z");

    [Fact]
    public void Resolve_SelectsLowestPriorityHealthyProviderForChat()
    {
        var store = CreateStore();
        store.DeleteModelProviderInstance("openai_default");
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", 10);
        SaveProvider(store, AnthropicBackupProviderId, "Anthropic Backup", "claude-4", 20);

        var resolved = new ModelRouteResolver(store).Resolve(ChatPurpose);

        Assert.Equal(OpenAiPrimaryProviderId, resolved.ProviderInstanceId);
        Assert.Equal("gpt-5.4", resolved.EffectiveModel);
        Assert.True(resolved.IsFallbackProvider);
    }

    [Fact]
    public void Resolve_FallsBackToHealthyBackupWhenPrimaryIsUnhealthyWithActiveCooldown()
    {
        var store = CreateStore();
        SaveProvider(
            store,
            OpenAiPrimaryProviderId,
            "OpenAI Primary",
            "gpt-5.4",
            10,
            healthMetadata: [$"health:unhealthy", $"cooldown_until:{FixedNow.AddMinutes(5):O}", "failure_count:3"]);
        SaveProvider(store, AnthropicBackupProviderId, "Anthropic Backup", "claude-4", 20);
        store.SaveModelRoute(ChatPurpose, OpenAiPrimaryProviderId, "gpt-5.4");

        var resolved = new ModelRouteResolver(store).Resolve(ChatPurpose);

        Assert.Equal(AnthropicBackupProviderId, resolved.ProviderInstanceId);
        Assert.Equal("claude-4", resolved.EffectiveModel);
    }

    [Fact]
    public void Resolve_ExcludesDisabledProviderEvenWhenItHasHighestPriority()
    {
        var store = CreateStore();
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", 1, false);
        SaveProvider(store, AnthropicBackupProviderId, "Anthropic Backup", "claude-4", 20);
        store.SaveModelRoute(ChatPurpose, OpenAiPrimaryProviderId, "gpt-5.4");

        var resolved = new ModelRouteResolver(store).Resolve(ChatPurpose);

        Assert.Equal(AnthropicBackupProviderId, resolved.ProviderInstanceId);
        Assert.True(resolved.Provider?.Enabled);
    }

    [Fact]
    public void Resolve_TreatsProviderInCooldownPeriodAsUnavailable()
    {
        var store = CreateStore();
        SaveProvider(
            store,
            OpenAiPrimaryProviderId,
            "OpenAI Primary",
            "gpt-5.4",
            10,
            healthMetadata:
            [
                $"health:cooldown", $"cooldown_started_at:{FixedNow.AddMinutes(-1):O}",
                $"cooldown_until:{FixedNow.AddMinutes(9):O}"
            ]);
        SaveProvider(store, AnthropicBackupProviderId, "Anthropic Backup", "claude-4", 20);
        store.SaveModelRoute(ChatPurpose, OpenAiPrimaryProviderId, "gpt-5.4");

        var resolved = new ModelRouteResolver(store).Resolve(ChatPurpose);

        Assert.Equal(AnthropicBackupProviderId, resolved.ProviderInstanceId);
    }

    [Fact]
    public void Resolve_UsesProviderIdTieBreakerForEqualPriorityProviders()
    {
        var store = CreateStore();
        SaveProvider(store, LocalHttpProviderId, "Local HTTP", "local-chat", 10, connectionKind: "local-http");
        SaveProvider(store, AnthropicBackupProviderId, "Anthropic Backup", "claude-4", 10);
        store.SaveModelRoute(ChatPurpose, LocalHttpProviderId, "local-chat");

        var resolved = new ModelRouteResolver(store).Resolve(ChatPurpose);

        Assert.Contains(
            resolved.ProviderInstanceId,
            [AnthropicBackupProviderId, LocalHttpProviderId],
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            resolved.ProviderInstanceId.Equals(AnthropicBackupProviderId, StringComparison.OrdinalIgnoreCase)
                ? "claude-4"
                : "local-chat",
            resolved.EffectiveModel);
    }

    [Fact]
    public void Resolve_ThrowsWhenNoProviderCanServeChatPurpose()
    {
        var store = CreateStore();
        store.DeleteModelProviderInstance("openai_default");

        var exception =
            Assert.Throws<InvalidOperationException>(() => new ModelRouteResolver(store).Resolve(ChatPurpose));
        Assert.Contains(ChatPurpose, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderHealthStatusChangesPersistAcrossStoreReload()
    {
        var db = CreateDatabasePath();
        var store = CreateStore(db);
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", 10);

        var recordFailure = typeof(CoreStore).GetMethod(
            "RecordModelProviderFailure",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [typeof(string), typeof(ProviderErrorCategory), typeof(DateTimeOffset)],
            null);
        Assert.NotNull(recordFailure);
        recordFailure.Invoke(store, [OpenAiPrimaryProviderId, ProviderErrorCategory.Timeout, FixedNow]);

        var reloaded = CreateStore(db);
        var resolved = new ModelRouteResolver(reloaded).Resolve(ChatPurpose);

        Assert.NotEqual(OpenAiPrimaryProviderId, resolved.ProviderInstanceId);
    }

    [Fact]
    public void ListModelProviderInstancesShowsActiveCooldownStatusAfterFailure()
    {
        var store = CreateStore();
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", 10);

        store.RecordModelProviderFailure(OpenAiPrimaryProviderId, ProviderErrorCategory.Timeout, DateTimeOffset.UtcNow);

        var provider = Assert.Single(store.ListModelProviderInstances(), item => item.Id == OpenAiPrimaryProviderId);
        Assert.Equal("cooldown", provider.Status);
        Assert.Contains("Timeout", provider.StatusMessage);
    }

    [Fact]
    public void CliProviderDtoShowsActiveCooldownInsteadOfReady()
    {
        var provider = new StoredModelProviderInstance(
            "provider-cli",
            "codex-cli",
            "Codex CLI",
            "cli",
            null,
            "gpt-5.4",
            null,
            "/bin/sh",
            Path.GetTempPath(),
            null,
            null,
            ["chat"],
            true,
            ProviderHealthStatus.Cooldown,
            DateTimeOffset.UtcNow.AddMinutes(5),
            1,
            DateTimeOffset.UtcNow,
            ProviderErrorCategory.Timeout,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var dto = provider.ToDto();

        Assert.Equal("cooldown", dto.Status);
        Assert.Contains("Timeout", dto.StatusMessage);
    }

    [Fact]
    public void ProviderDtoExposesCooldownUntil()
    {
        var cooldownUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        var provider = new StoredModelProviderInstance(
            "provider-cooldown",
            "openai-compatible",
            "OpenAI",
            "api-key",
            "https://api.example.test/v1",
            "gpt-5.4",
            "encrypted-key",
            null,
            null,
            null,
            null,
            ["chat"],
            true,
            ProviderHealthStatus.Cooldown,
            cooldownUntil,
            1,
            DateTimeOffset.UtcNow,
            ProviderErrorCategory.RateLimited,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var dto = provider.ToDto();

        Assert.Equal(cooldownUntil, dto.CooldownUntil);
    }

    [Fact]
    public void RecordModelProviderSuccessClearsCooldownAndRestoresHealthyStatus()
    {
        var store = CreateStore();
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", 10);

        store.RecordModelProviderFailure(OpenAiPrimaryProviderId, ProviderErrorCategory.Timeout, DateTimeOffset.UtcNow);
        var afterFailure = store.GetStoredModelProviderInstance(OpenAiPrimaryProviderId)!;
        Assert.Equal(ProviderHealthStatus.Cooldown, afterFailure.HealthStatus);
        Assert.NotNull(afterFailure.CooldownUntil);

        store.RecordModelProviderSuccess(OpenAiPrimaryProviderId);
        var afterSuccess = store.GetStoredModelProviderInstance(OpenAiPrimaryProviderId)!;
        Assert.Equal(ProviderHealthStatus.Healthy, afterSuccess.HealthStatus);
        Assert.Null(afterSuccess.CooldownUntil);
        Assert.Equal(0, afterSuccess.FailureCount);
        Assert.Equal("health:healthy",
            afterSuccess.Capabilities.First(c => c.StartsWith("health:", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void RecordModelProviderSuccessIsNoOpForAlreadyHealthyProvider()
    {
        var store = CreateStore();
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", 10);

        var before = store.GetStoredModelProviderInstance(OpenAiPrimaryProviderId)!;
        Assert.Equal(ProviderHealthStatus.Healthy, before.HealthStatus);

        store.RecordModelProviderSuccess(OpenAiPrimaryProviderId);
        var after = store.GetStoredModelProviderInstance(OpenAiPrimaryProviderId)!;
        Assert.Equal(ProviderHealthStatus.Healthy, after.HealthStatus);
    }

    private static CoreStore CreateStore(string? databasePath = null)
    {
        var store = new CoreStore(databasePath ?? CreateDatabasePath());
        store.Initialize();
        return store;
    }

    private static string CreateDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"tinadec-route-resolver-{Guid.NewGuid():N}.db");
    }

    private static void SaveProvider(
        CoreStore store,
        string id,
        string displayName,
        string model,
        int priority,
        bool enabled = true,
        string connectionKind = "test-connection",
        IReadOnlyList<string>? healthMetadata = null)
    {
        var capabilities = new List<string>
        {
            "chat",
            $"route:{ChatPurpose}",
            $"priority:{priority}",
            $"clock:{FixedNow:O}",
            "no-api-key"
        };

        if (healthMetadata is not null) capabilities.AddRange(healthMetadata);

        var baseUrl = connectionKind.Equals("local-http", StringComparison.OrdinalIgnoreCase)
            ? "http://127.0.0.1:11434/v1"
            : $"https://{id}.example.test/v1";

        store.SaveModelProviderInstance(
            new SaveModelProviderInstanceRequest(
                id,
                "openai-compatible",
                displayName,
                connectionKind,
                baseUrl,
                model,
                null,
                false,
                null,
                null,
                null,
                null,
                capabilities,
                enabled),
            null);
    }
}
