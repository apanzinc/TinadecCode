using System.Diagnostics;
using Tinadec.Contracts.Models;
using TinadecModel.Abstractions;
using TinadecModel.Tracing;

namespace TinadecModel.Services;

public sealed class ModelInvocationRuntime(
    IModelRouteResolver routeResolver,
    IModelCredentialResolver credentialResolver,
    IEnumerable<IModelProviderRuntime> providerRuntimes,
    IModelStore? store = null) : IModelInvocationRuntime
{
    public async Task<ModelInvocationResultDto> InvokeAsync(
        string sessionId, string purpose, IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken = default,
        string? systemPrompt = null, IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var requestMessages = BuildRequestMessages(sessionId, messages, systemPrompt);
        using var activity = ModelActivitySource.Instance.StartActivity(ModelSpanNames.ModelProviderInvocation);
        activity?.SetTag(ModelSpanAttrs.SessionId, sessionId)
            .SetTag(ModelSpanAttrs.RoutePurpose, purpose)
            .SetTag(ModelSpanAttrs.MessageCount, requestMessages.Count);

        ModelInvocationResultDto? firstFailure = null;
        ModelInvocationResultDto? lastFailure = null;
        var attemptedProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            ModelInvocationResultDto result;
            try
            {
                result = await InvokeResolvedProviderAsync(purpose, requestMessages, cancellationToken, tools);
            }
            catch (InvalidOperationException)
            {
                var terminalContext = lastFailure?.Context ?? firstFailure?.Context
                    ?? throw new InvalidOperationException("Model invocation did not produce a result.");
                var terminalContent = lastFailure is not null
                    ? $"All model providers failed. Last error: {lastFailure.Content}. No fallback provider is available."
                    : $"No model provider is available for purpose '{purpose}'.";
                return new ModelInvocationResultDto("failed", terminalContent, terminalContext, false, null,
                    ProviderErrorCategory.ProviderUnavailable, false, null, null, terminalContent, null);
            }

            if (string.Equals(result.Status, "executed", StringComparison.OrdinalIgnoreCase))
            {
                if (firstFailure is not null && store is not null)
                {
                    var recoveredProviderId = result.Context.ProviderInstanceId;
                    store.RecordModelProviderSuccess(recoveredProviderId);
                }

                return firstFailure is null
                    ? result
                    : result with
                    {
                        ErrorProviderId = firstFailure.ErrorProviderId ?? firstFailure.Context.ProviderInstanceId
                    };
            }

            firstFailure ??= result;
            lastFailure = result;
            if (!ShouldTryFallback(result, attemptedProviderIds))
                return result;

            attemptedProviderIds.Add(result.ErrorProviderId ?? result.Context.ProviderInstanceId);
            if (store is not null && result.ErrorCategory is { } category && !RuntimeRecordsRetryableFailure(result))
                store.RecordModelProviderFailure(result.ErrorProviderId ?? result.Context.ProviderInstanceId, category,
                    DateTimeOffset.UtcNow);
        }

        return lastFailure ?? firstFailure ??
            throw new InvalidOperationException("Model invocation did not produce a result.");
    }

    private async Task<ModelInvocationResultDto> InvokeResolvedProviderAsync(
        string purpose, IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken, IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var context = routeResolver.Resolve(purpose);
        using var activity = ModelActivitySource.Instance.StartActivity(ModelSpanNames.ModelRequest);
        activity?.SetTag(ModelSpanAttrs.RoutePurpose, context.Purpose)
            .SetTag(ModelSpanAttrs.ProviderId, context.ProviderInstanceId)
            .SetTag(ModelSpanAttrs.ProviderInstanceId, context.ProviderInstanceId)
            .SetTag(ModelSpanAttrs.Model, context.EffectiveModel);

        var apiKey = credentialResolver.ResolveApiKey(context);
        var credentialValidation = ProviderCredentialValidator.Validate(context, apiKey);
        if (!credentialValidation.IsValid)
            return new ModelInvocationResultDto("failed",
                credentialValidation.SafeMessage ?? "Provider authentication failed.",
                context, true, null, credentialValidation.ErrorCategory, false, null, null,
                credentialValidation.SafeMessage, context.ProviderInstanceId);

        var runtime = providerRuntimes
            .Where(r => r.CanHandle(context))
            .OrderByDescending(r => string.Equals(r.Id, context.ProviderInstanceId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => string.Equals(r.Id, context.Driver, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        if (runtime is null)
            return new ModelInvocationResultDto("failed",
                $"No model runtime is registered for provider '{context.ProviderInstanceId}' (connection kind: {context.ConnectionKind}).",
                context, true, null);

        var result = await runtime.GenerateAsync(context, apiKey, messages, cancellationToken, tools);
        activity?.SetTag(ModelSpanAttrs.Status, result.Status)
            .SetTag(ModelSpanAttrs.ErrorCategory, result.ErrorCategory?.ToString());
        return result;
    }

    /// <summary>
    /// 流式调用入口。先发送 context chunk（携带 provider/model 元信息），
    /// 再逐个推送 delta，最后发送 done/error chunk。
    /// 不支持流式的 runtime 自动回退到 GenerateAsync，将完整内容作为单个 delta 推出。
    /// </summary>
    public async IAsyncEnumerable<ModelStreamChunkDto> InvokeStreamAsync(
        string sessionId, string purpose, IReadOnlyList<MessageDto> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default,
        string? systemPrompt = null, IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var requestMessages = BuildRequestMessages(sessionId, messages, systemPrompt);
        using var activity = ModelActivitySource.Instance.StartActivity(ModelSpanNames.ModelProviderInvocation);
        activity?.SetTag(ModelSpanAttrs.SessionId, sessionId)
            .SetTag(ModelSpanAttrs.RoutePurpose, purpose)
            .SetTag(ModelSpanAttrs.MessageCount, requestMessages.Count);

        var attemptedProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ModelInvocationResultDto? firstFailure = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var resolved = ResolveStreamContext(purpose, cancellationToken);
            if (resolved.Error is not null)
            {
                yield return resolved.Error;
                yield break;
            }

            var context = resolved.Context!;
            var apiKey = resolved.ApiKey;
            var runtime = resolved.Runtime;

            // 推送 context chunk，让前端知道当前使用的 provider/model
            yield return new ModelStreamChunkDto(
                string.Empty,
                sessionId,
                purpose,
                context.ProviderInstanceId,
                context.EffectiveModel,
                ModelStreamChunkKind.Context,
                null,
                FallbackProviderSelected: firstFailure is not null,
                ErrorProviderId: firstFailure?.ErrorProviderId ?? firstFailure?.Context.ProviderInstanceId);

            var streamedAny = false;
            var contentBuilder = new System.Text.StringBuilder();
            ToolCallDto? lastToolCall = null;
            ModelUsageDto? usage = null;
            var finishReason = ModelFinishReason.Unknown;

            if (runtime is not null)
            {
                IAsyncEnumerable<ModelStreamChunkDto>? stream = null;
                try
                {
                    stream = runtime.StreamAsync(context, apiKey, requestMessages, cancellationToken, tools);
                }
                catch (NotSupportedException)
                {
                    stream = null; // 回退到非流式
                }

                if (stream is not null)
                {
                    streamedAny = true;
                    await foreach (var chunk in stream.WithCancellation(cancellationToken))
                    {
                        if (!string.IsNullOrEmpty(chunk.Delta))
                            contentBuilder.Append(chunk.Delta);
                        if (chunk.ToolCallDelta is not null)
                            lastToolCall = chunk.ToolCallDelta;
                        if (chunk.Usage is not null)
                            usage = chunk.Usage;
                        if (chunk.FinishReason is not null)
                            finishReason = chunk.FinishReason.Value;

                        yield return chunk with
                        {
                            RunId = string.Empty,
                            SessionId = sessionId,
                            Purpose = purpose,
                            ProviderInstanceId = context.ProviderInstanceId,
                            EffectiveModel = context.EffectiveModel,
                            FallbackProviderSelected = firstFailure is not null,
                            ErrorProviderId = firstFailure?.ErrorProviderId ?? firstFailure?.Context.ProviderInstanceId
                        };
                    }
                }
            }

            if (!streamedAny)
            {
                // 回退：调用非流式 GenerateAsync，将完整内容作为单个 delta 推送
                ModelInvocationResultDto? result = null;
                ModelStreamChunkDto? catchErrorChunk = null;
                try
                {
                    result = runtime is null
                        ? new ModelInvocationResultDto("failed",
                            $"No model runtime is registered for provider '{context.ProviderInstanceId}'.",
                            context, true, null)
                        : await runtime.GenerateAsync(context, apiKey, requestMessages, cancellationToken, tools);
                }
                catch (InvalidOperationException ex)
                {
                    catchErrorChunk = new ModelStreamChunkDto(
                        string.Empty, sessionId, purpose, context.ProviderInstanceId, context.EffectiveModel,
                        ModelStreamChunkKind.Error, null, null, null, null,
                        ProviderErrorCategory.ProviderUnavailable, false, false, ex.Message,
                        context.ProviderInstanceId);
                }

                if (catchErrorChunk is not null)
                {
                    yield return catchErrorChunk;
                    yield break;
                }

                if (!string.Equals(result!.Status, "executed", StringComparison.OrdinalIgnoreCase))
                {
                    firstFailure ??= result;
                    if (ShouldTryFallback(result, attemptedProviderIds))
                    {
                        attemptedProviderIds.Add(result.ErrorProviderId ?? context.ProviderInstanceId);
                        if (store is not null && result.ErrorCategory is { } category &&
                            !RuntimeRecordsRetryableFailure(result))
                            store.RecordModelProviderFailure(result.ErrorProviderId ?? context.ProviderInstanceId,
                                category, DateTimeOffset.UtcNow);
                        continue; // 尝试 fallback
                    }

                    yield return new ModelStreamChunkDto(
                        string.Empty, sessionId, purpose, context.ProviderInstanceId, context.EffectiveModel,
                        ModelStreamChunkKind.Error, null, null, null, null,
                        result.ErrorCategory, result.IsRetryable, false, result.SafeErrorMessage ?? result.Content,
                        result.ErrorProviderId ?? context.ProviderInstanceId);
                    yield break;
                }

                contentBuilder.Append(result.Content);
                lastToolCall = result.ToolCalls is { Count: > 0 } ? result.ToolCalls[0] : null;
                finishReason = ModelFinishReason.Stop;
                if (firstFailure is not null && store is not null)
                    store.RecordModelProviderSuccess(context.ProviderInstanceId);

                yield return new ModelStreamChunkDto(
                    string.Empty, sessionId, purpose, context.ProviderInstanceId, context.EffectiveModel,
                    ModelStreamChunkKind.Delta, result.Content, lastToolCall, null, finishReason,
                    null, false, true, null, firstFailure?.ErrorProviderId ?? firstFailure?.Context.ProviderInstanceId);
            }
            else if (firstFailure is not null && store is not null)
            {
                store.RecordModelProviderSuccess(context.ProviderInstanceId);
            }

            // done chunk
            yield return new ModelStreamChunkDto(
                string.Empty, sessionId, purpose, context.ProviderInstanceId, context.EffectiveModel,
                ModelStreamChunkKind.Done, null, lastToolCall, usage, finishReason,
                null, false, firstFailure is not null, null,
                firstFailure?.ErrorProviderId ?? firstFailure?.Context.ProviderInstanceId);
            yield break;
        }

        // 所有 fallback 都失败
        var terminalContext = firstFailure?.Context;
        yield return new ModelStreamChunkDto(
            string.Empty, sessionId, purpose, terminalContext?.ProviderInstanceId ?? string.Empty,
            terminalContext?.EffectiveModel, ModelStreamChunkKind.Error, null, null, null, null,
            ProviderErrorCategory.ProviderUnavailable, false, false,
            firstFailure?.SafeErrorMessage ?? $"No model provider is available for purpose '{purpose}'.",
            terminalContext?.ProviderInstanceId);
    }

    private readonly record struct StreamResolution(
        ResolvedModelInvocationContextDto? Context,
        string? ApiKey,
        IModelProviderRuntime? Runtime,
        ModelStreamChunkDto? Error);

    private StreamResolution ResolveStreamContext(string purpose, CancellationToken cancellationToken)
    {
        ResolvedModelInvocationContextDto context;
        try
        {
            context = routeResolver.Resolve(purpose);
        }
        catch (InvalidOperationException ex)
        {
            return new StreamResolution(null, null, null, new ModelStreamChunkDto(
                string.Empty, string.Empty, purpose, string.Empty, null,
                ModelStreamChunkKind.Error, null, null, null, null,
                ProviderErrorCategory.ProviderUnavailable, false, false, ex.Message, null));
        }

        var apiKey = credentialResolver.ResolveApiKey(context);
        var credentialValidation = ProviderCredentialValidator.Validate(context, apiKey);
        if (!credentialValidation.IsValid)
            return new StreamResolution(context, apiKey, null, new ModelStreamChunkDto(
                string.Empty, string.Empty, purpose, context.ProviderInstanceId, context.EffectiveModel,
                ModelStreamChunkKind.Error, null, null, null, null,
                credentialValidation.ErrorCategory, false, false,
                credentialValidation.SafeMessage ?? "Provider authentication failed.", context.ProviderInstanceId));

        var runtime = providerRuntimes
            .Where(r => r.CanHandle(context))
            .OrderByDescending(r => string.Equals(r.Id, context.ProviderInstanceId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => string.Equals(r.Id, context.Driver, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();

        return new StreamResolution(context, apiKey, runtime, null);
    }

    private static IReadOnlyList<MessageDto> BuildRequestMessages(string sessionId, IReadOnlyList<MessageDto> messages,
        string? systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt)) return messages;
        var systemMessage = new MessageDto($"sys_{Guid.NewGuid():N}", sessionId, "system", systemPrompt.Trim(),
            DateTimeOffset.UtcNow);
        return [systemMessage, .. messages];
    }

    private static bool ShouldTryFallback(ModelInvocationResultDto result, HashSet<string> attemptedProviderIds)
    {
        var failedProviderId = result.ErrorProviderId ?? result.Context.ProviderInstanceId;
        return result.IsRetryable && result.ErrorCategory is not null &&
               !attemptedProviderIds.Contains(failedProviderId);
    }

    private static bool RuntimeRecordsRetryableFailure(ModelInvocationResultDto result)
    {
        return result.IsRetryable
               && (string.Equals(result.RuntimeId, "openai-compatible", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(result.RuntimeId, "cli-provider", StringComparison.OrdinalIgnoreCase));
    }
}
