using Tinadec.Contracts.Models;
using TinadecModel.Abstractions;
using TinadecModel.Storage;
using TinadecModel.Services;

namespace TinadecModel.Providers;

public sealed class OpenAiCompatibleProviderRuntime(
    OpenAiCompatibleClient client,
    IModelStore? store = null,
    int maxRetryAttempts = 3) : IModelProviderRuntime
{
    private readonly ProviderExecutionPolicy _policy = new(Math.Max(1, maxRetryAttempts));

    public string Id => "openai-compatible";

    public bool CanHandle(ResolvedModelInvocationContextDto context)
    {
        return !ProviderTemplateRules.IsLocalOpenAiCompatibleDriver(context.Driver)
               && !ProviderTemplateRules.IsLocalOpenAiCompatibleDriver(context.Provider?.Driver)
               && (ProviderTemplateRules.IsOpenAiCompatibleDriver(context.Driver)
                   || ProviderTemplateRules.IsOpenAiCompatibleDriver(context.Provider?.Driver));
    }

    public async Task<ModelInvocationResultDto> GenerateAsync(
        ResolvedModelInvocationContextDto context, string? apiKey,
        IReadOnlyList<MessageDto> messages, CancellationToken cancellationToken = default,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var outcome = await ProviderPolicyHelpers.ExecuteAsync(
            context.ProviderInstanceId,
            async executionToken =>
            {
                var settings = new StoredModelSettings(
                    context.EffectiveBaseUrl, context.EffectiveModel,
                    context.EncryptedApiKey, DateTimeOffset.UtcNow);
                return await client.CreateAssistantResponseAsync(
                    settings, apiKey, messages, context.ProviderInstanceId,
                    executionToken, tools);
            },
            exception => ProviderErrorMapper.FromException(context.ProviderInstanceId, exception),
            _policy, store, cancellationToken);

        if (outcome.Succeeded)
        {
            var response = outcome.Value!;
            return new ModelInvocationResultDto("executed", response.TextContent, context, false, Id,
                ToolCalls: response.ToolCalls);
        }

        var failure = outcome.Failure!;
        return new ModelInvocationResultDto("failed", failure.SafeMessage, context, false, Id,
            failure.Category, failure.Retryable, failure.StatusCode, failure.ExitCode,
            failure.SafeMessage, failure.ProviderId);
    }

    /// <summary>
    /// 流式生成。直接委托给 client 的 SSE 解析器，不做重试（流式重试会导致重复输出）。
    /// 失败时抛出异常，由上层 InvokeStreamAsync 决定是否 fallback。
    /// </summary>
    public async IAsyncEnumerable<ModelStreamChunkDto> StreamAsync(
        ResolvedModelInvocationContextDto context, string? apiKey,
        IReadOnlyList<MessageDto> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var settings = new StoredModelSettings(
            context.EffectiveBaseUrl, context.EffectiveModel,
            context.EncryptedApiKey, DateTimeOffset.UtcNow);

        IAsyncEnumerable<ModelStreamChunkDto>? stream = null;
        ModelStreamChunkDto? errorChunk = null;
        try
        {
            stream = client.StreamChatCompletionsAsync(
                settings, apiKey, messages, context.ProviderInstanceId,
                cancellationToken, tools);
        }
        catch (Exception ex)
        {
            var error = ProviderErrorMapper.FromException(context.ProviderInstanceId, ex);
            errorChunk = new ModelStreamChunkDto(
                string.Empty, string.Empty, string.Empty, context.ProviderInstanceId, context.EffectiveModel,
                ModelStreamChunkKind.Error, null, null, null, null,
                error.Category, error.Retryable, false, error.SafeMessage, error.ProviderId);
        }

        if (errorChunk is not null)
        {
            yield return errorChunk;
            yield break;
        }

        await foreach (var chunk in stream!.WithCancellation(cancellationToken)) yield return chunk;
    }
}
