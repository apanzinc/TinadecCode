using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TinadecTools.Abstractions;

internal delegate ValueTask<ToolCallResponse<JsonElement>> ToolHandlerDelegate(
    ToolCallRequest<JsonElement> request,
    CancellationToken cancellationToken);

internal static class ToolRegistry
{
    private static readonly Dictionary<string, ToolHandlerDelegate> Handlers = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string toolId, ToolHandlerDelegate handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(handler);

        Handlers[toolId] = handler;
    }

    public static void Register<TArgs, TResult>(ToolHandlerBase<TArgs, TResult> handler)
        where TArgs : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(handler.ToolId, handler.HandleAsync);
    }

    public static void Register<TArgs, TResult>(
        string toolId,
        Func<TArgs, CancellationToken, ValueTask<TResult>> handler,
        JsonTypeInfo<TArgs> argsTypeInfo,
        JsonTypeInfo<TResult> resultTypeInfo,
        bool requiresApproval = false)
        where TArgs : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(argsTypeInfo);
        ArgumentNullException.ThrowIfNull(resultTypeInfo);

        Register(toolId, async (request, cancellationToken) =>
        {
            if (requiresApproval && !request.Approved)
            {
                return new ToolCallResponse<JsonElement>
                {
                    CallId = request.ToolCallId,
                    IsSuccess = false,
                    Response = JsonSerializer.SerializeToElement(NotApprovedResponse.MESSAGE, ToolCallJsonContext.Default.String)
                };
            }

            if (request.Params.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new InvalidOperationException($"Tool '{request.ToolId}' requires params.");

            var args = JsonSerializer.Deserialize(request.Params, argsTypeInfo)
                       ?? throw new InvalidOperationException($"Tool '{request.ToolId}' params could not be parsed.");

            var result = await handler(args, cancellationToken).ConfigureAwait(false);

            return new ToolCallResponse<JsonElement>
            {
                CallId = request.ToolCallId,
                IsSuccess = true,
                Response = JsonSerializer.SerializeToElement(result, resultTypeInfo)
            };
        });
    }

    public static bool TryResolve(string toolId, out ToolHandlerDelegate handler)
    {
        return Handlers.TryGetValue(toolId, out handler!);
    }

    public static ValueTask<ToolCallResponse<JsonElement>> DispatchAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolve(request.ToolId, out var handler))
            throw new InvalidOperationException($"Unknown tool '{request.ToolId}'.");

        return handler(request, cancellationToken);
    }
}
