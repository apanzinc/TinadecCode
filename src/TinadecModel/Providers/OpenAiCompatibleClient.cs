using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TinadecModel.Storage;
using Tinadec.Contracts.Models;
using TinadecModel.Tracing;
using TinadecModel.Json;

namespace TinadecModel.Providers;

public sealed class OpenAiCompatibleClient(HttpClient httpClient)
{
    public async Task<string> CreateAssistantReplyAsync(
        StoredModelSettings settings,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken)
    {
        var response = await CreateAssistantResponseAsync(settings, apiKey, messages, null, cancellationToken);
        return response.TextContent;
    }

    public async Task<ModelInvocationResponseDto> CreateAssistantResponseAsync(
        StoredModelSettings settings,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        string? providerId,
        CancellationToken cancellationToken,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        using var activity = ModelActivitySource.Instance.StartActivity(ModelSpanNames.AgentInference);
        activity?
            .SetTag(ModelSpanAttrs.Model, settings.Model)
            .SetTag(ModelSpanAttrs.BaseUrl, settings.BaseUrl)
            .SetTag(ModelSpanAttrs.HasApiKey, !string.IsNullOrWhiteSpace(apiKey));

        if (string.IsNullOrWhiteSpace(settings.BaseUrl) ||
            string.IsNullOrWhiteSpace(settings.Model))
            return CreateResponse(
                "TinadecOffice Core is running. Add an OpenAI-compatible base URL and model to enable live model responses.",
                new ModelUsageDto(0, 0, 0),
                ModelFinishReason.Unknown,
                settings,
                providerId,
                null,
                null,
                null,
                null);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var request = BuildChatCompletionRequest(settings, apiKey, messages);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            activity?.SetTag(ModelSpanAttrs.StatusCode, (int)response.StatusCode);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error,
                $"Model request failed with {(int)response.StatusCode}");
            throw new HttpRequestException(
                $"Model request failed with {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        sw.Stop();
        activity?.SetTag(ModelSpanAttrs.LatencyMs, sw.ElapsedMilliseconds);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0];
        var messageElement = choice.GetProperty("message");
        var content = messageElement.TryGetProperty("content", out var contentProp) &&
                      contentProp.ValueKind == JsonValueKind.String
            ? contentProp.GetString()
            : null;

        var textContent = string.IsNullOrWhiteSpace(content)
            ? "The model returned an empty response."
            : content;

        // Parse tool_calls from the response
        IReadOnlyList<ToolCallDto>? parsedToolCalls = null;
        if (messageElement.TryGetProperty("tool_calls", out var toolCallsElement)
            && toolCallsElement.ValueKind == JsonValueKind.Array
            && toolCallsElement.GetArrayLength() > 0)
        {
            var calls = new List<ToolCallDto>();
            foreach (var tc in toolCallsElement.EnumerateArray())
            {
                var callId = tc.GetProperty("id").GetString() ?? $"call_{Guid.NewGuid():N}";
                var function = tc.GetProperty("function");
                var functionName = function.GetProperty("name").GetString() ?? "unknown";
                var argsJson = function.TryGetProperty("arguments", out var argsProp)
                    ? argsProp.GetString() ?? "{}"
                    : "{}";
                var arguments = ParseToolArguments(argsJson);
                calls.Add(new ToolCallDto(callId, functionName, arguments));
            }

            parsedToolCalls = calls;
        }

        return CreateResponse(
            textContent,
            ReadUsage(root),
            ReadFinishReason(choice),
            settings,
            providerId,
            ReadString(root, "id"),
            ReadString(root, "object"),
            ReadInt64(root, "created"),
            root.GetProperty("choices").GetArrayLength(),
            parsedToolCalls);
    }

    public static HttpRequestMessage BuildChatCompletionRequest(
        StoredModelSettings settings,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var endpoint = BuildChatCompletionsEndpoint(settings.BaseUrl);
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var messageList = messages.Select(message =>
        {
            var msg = new Dictionary<string, object?>
            {
                ["role"] = message.Role,
                ["content"] = message.Content
            };
            if (!string.IsNullOrWhiteSpace(message.ToolCallId)) msg["tool_call_id"] = message.ToolCallId;
            return msg;
        }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["stream"] = false,
            ["messages"] = messageList
        };

        if (tools is { Count: > 0 })
            payload["tools"] = tools.Select(tool => new
            {
                type = tool.Type,
                function = new
                {
                    name = tool.Function.Name,
                    description = tool.Function.Description,
                    parameters = tool.Function.Parameters
                }
            }).ToArray();

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, TinadecJson.Options),
            Encoding.UTF8,
            "application/json");

        return request;
    }

    /// <summary>
    /// 构建流式 chat/completions 请求（stream=true）。响应是 SSE 格式。
    /// </summary>
    public static HttpRequestMessage BuildStreamingChatCompletionRequest(
        StoredModelSettings settings,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var endpoint = BuildChatCompletionsEndpoint(settings.BaseUrl);
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var messageList = messages.Select(message =>
        {
            var msg = new Dictionary<string, object?>
            {
                ["role"] = message.Role,
                ["content"] = message.Content
            };
            if (!string.IsNullOrWhiteSpace(message.ToolCallId)) msg["tool_call_id"] = message.ToolCallId;
            return msg;
        }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["stream"] = true,
            ["messages"] = messageList,
            ["stream_options"] = new { include_usage = true }
        };

        if (tools is { Count: > 0 })
            payload["tools"] = tools.Select(tool => new
            {
                type = tool.Type,
                function = new
                {
                    name = tool.Function.Name,
                    description = tool.Function.Description,
                    parameters = tool.Function.Parameters
                }
            }).ToArray();

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, TinadecJson.Options),
            Encoding.UTF8,
            "application/json");

        return request;
    }

    /// <summary>
    /// 流式调用 OpenAI 兼容端点。逐行解析 SSE，产出 delta chunk。
    /// </summary>
    public async IAsyncEnumerable<ModelStreamChunkDto> StreamChatCompletionsAsync(
        StoredModelSettings settings,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        string providerInstanceId,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
        {
            // 无配置时回退到 stub 响应
            yield return new ModelStreamChunkDto(
                string.Empty, string.Empty, string.Empty, providerInstanceId, settings.Model,
                ModelStreamChunkKind.Delta,
                "TinadecOffice Core is running. Add an OpenAI-compatible base URL and model to enable live model responses.",
                null, null, ModelFinishReason.Stop, null, false, false, null, null);
            yield break;
        }

        using var request = BuildStreamingChatCompletionRequest(settings, apiKey, messages, tools);
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Streaming model request failed with {(int)response.StatusCode}: {errorBody[..Math.Min(200, errorBody.Length)]}",
                null, response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        ModelUsageDto? finalUsage = null;
        var finalFinishReason = ModelFinishReason.Unknown;
        ToolCallDto? accumulatedToolCall = null;

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
            {
                yield return new ModelStreamChunkDto(
                    string.Empty, string.Empty, string.Empty, providerInstanceId, settings.Model,
                    ModelStreamChunkKind.Done, null, accumulatedToolCall, finalUsage, finalFinishReason,
                    null, false, false, null, null);
                yield break;
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;

            // 解析 usage（stream_options.include_usage=true 时最后一个 chunk 会带）
            if (root.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
            {
                finalUsage = new ModelUsageDto(
                    ReadInt32(usageProp, "prompt_tokens") ?? 0,
                    ReadInt32(usageProp, "completion_tokens") ?? 0,
                    ReadInt32(usageProp, "total_tokens") ?? 0);
                yield return new ModelStreamChunkDto(
                    string.Empty, string.Empty, string.Empty, providerInstanceId, settings.Model,
                    ModelStreamChunkKind.Usage, null, null, finalUsage, null, null, false, false, null, null);
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;

            var choice = choices[0];
            var finishReasonStr = ReadString(choice, "finish_reason");
            if (finishReasonStr is not null)
                finalFinishReason = finishReasonStr switch
                {
                    "stop" => ModelFinishReason.Stop,
                    "length" => ModelFinishReason.Length,
                    "content_filter" => ModelFinishReason.ContentFilter,
                    "tool_calls" or "function_call" => ModelFinishReason.ToolCalls,
                    _ => ModelFinishReason.Unknown
                };

            if (!choice.TryGetProperty("delta", out var delta)) continue;

            // 文本 delta
            if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
            {
                var deltaText = contentProp.GetString();
                if (!string.IsNullOrEmpty(deltaText))
                    yield return new ModelStreamChunkDto(
                        string.Empty, string.Empty, string.Empty, providerInstanceId, settings.Model,
                        ModelStreamChunkKind.Delta, deltaText, null, null, null, null, false, false, null, null);
            }

            // tool_calls delta
            if (delta.TryGetProperty("tool_calls", out var toolCallsProp) &&
                toolCallsProp.ValueKind == JsonValueKind.Array)
                foreach (var tc in toolCallsProp.EnumerateArray())
                {
                    var index = ReadInt32(tc, "index") ?? 0;
                    var callId = ReadString(tc, "id");
                    var function = tc.TryGetProperty("function", out var funcProp) ? funcProp : default;
                    var name = function.ValueKind == JsonValueKind.Object ? ReadString(function, "name") : null;
                    var argsDelta = function.ValueKind == JsonValueKind.Object
                        ? ReadString(function, "arguments")
                        : null;

                    if (callId is not null && name is not null)
                        accumulatedToolCall = new ToolCallDto(callId, name, new Dictionary<string, object?>());
                    if (argsDelta is not null && accumulatedToolCall is not null)
                        // 累积 arguments JSON 片段，这里简化处理：直接推送 delta
                        yield return new ModelStreamChunkDto(
                            string.Empty, string.Empty, string.Empty, providerInstanceId, settings.Model,
                            ModelStreamChunkKind.ToolCallDelta, argsDelta, accumulatedToolCall, null, null, null, false,
                            false, null, null);
                }
        }

        // 流自然结束但没收到 [DONE]
        yield return new ModelStreamChunkDto(
            string.Empty, string.Empty, string.Empty, providerInstanceId, settings.Model,
            ModelStreamChunkKind.Done, null, accumulatedToolCall, finalUsage, finalFinishReason,
            null, false, false, null, null);
    }

    public static Uri BuildChatCompletionsEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) trimmed += "/chat/completions";

        return new Uri(trimmed, UriKind.Absolute);
    }

    private static ModelInvocationResponseDto CreateResponse(
        string textContent,
        ModelUsageDto usage,
        ModelFinishReason finishReason,
        StoredModelSettings settings,
        string? providerId,
        string? responseId,
        string? responseObject,
        long? created,
        int? choiceCount,
        IReadOnlyList<ToolCallDto>? toolCalls = null)
    {
        var custom = new Dictionary<string, object?>();
        AddIfPresent(custom, "response_id", responseId);
        AddIfPresent(custom, "response_object", responseObject);
        AddIfPresent(custom, "created", created);
        AddIfPresent(custom, "choice_count", choiceCount);
        if (toolCalls is { Count: > 0 })
            custom["tool_calls"] = toolCalls.Select(tc => new Dictionary<string, object?>
            {
                ["call_id"] = tc.CallId,
                ["tool_id"] = tc.ToolId,
                ["argument_keys"] = tc.Arguments.Keys.OrderBy(k => k).ToArray()
            }).ToArray();

        return new ModelInvocationResponseDto(
            textContent,
            usage,
            finishReason,
            new ProviderMetadataDto(
                providerId ?? "openai-compatible",
                settings.Model,
                "openai-compatible",
                custom),
            null,
            null,
            null,
            toolCalls);
    }

    private static ModelUsageDto ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)) return new ModelUsageDto(0, 0, 0);

        var promptTokens = ReadInt32(usage, "prompt_tokens") ?? 0;
        var completionTokens = ReadInt32(usage, "completion_tokens") ?? 0;
        var totalTokens = ReadInt32(usage, "total_tokens") ?? promptTokens + completionTokens;
        return new ModelUsageDto(promptTokens, completionTokens, totalTokens);
    }

    private static ModelFinishReason ReadFinishReason(JsonElement choice)
    {
        var finishReason = ReadString(choice, "finish_reason");
        return finishReason switch
        {
            "stop" => ModelFinishReason.Stop,
            "length" => ModelFinishReason.Length,
            "content_filter" => ModelFinishReason.ContentFilter,
            "tool_calls" or "function_call" => ModelFinishReason.ToolCalls,
            "cancelled" => ModelFinishReason.Cancelled,
            null or "" => ModelFinishReason.Unknown,
            _ => ModelFinishReason.Unknown
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? ReadInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static long? ReadInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static void AddIfPresent(Dictionary<string, object?> custom, string key, object? value)
    {
        if (value is not null) custom[key] = value;
    }

    private static IReadOnlyDictionary<string, object?> ParseToolArguments(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson) || argsJson == "{}") return new Dictionary<string, object?>();

        try
        {
            using var document = JsonDocument.Parse(argsJson);
            var result = new Dictionary<string, object?>();
            foreach (var property in document.RootElement.EnumerateObject())
                result[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.TryGetInt64(out var l) ? l : property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText()
                };
            return result;
        }
        catch
        {
            return new Dictionary<string, object?> { ["raw"] = argsJson };
        }
    }
}
