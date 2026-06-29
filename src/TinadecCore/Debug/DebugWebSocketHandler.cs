using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TinadecCore.Services;
using TinadecCore.Tracing;

namespace TinadecCore.Debug;

/// <summary>
/// Handles WebSocket connections for the Agent Debug Studio real-time feed.
/// Pushes span events, metric samples, agent state changes, and breakpoint hits
/// to connected Debug Studio clients.
/// </summary>
public sealed class DebugWebSocketHandler
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private readonly EventHub _eventHub;
    private readonly TinadecMetrics _metrics;
    private readonly BreakpointService _breakpoints;

    public DebugWebSocketHandler(
        EventHub eventHub,
        TinadecMetrics metrics,
        BreakpointService breakpoints)
    {
        _eventHub = eventHub;
        _metrics = metrics;
        _breakpoints = breakpoints;
    }

    /// <summary>
    /// Handle an incoming WebSocket connection for the debug feed.
    /// </summary>
    public async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var connectionId = Guid.NewGuid().ToString();
        _connections[connectionId] = webSocket;

        // Subscribe to EventHub and forward events
        var eventTask = ForwardEventHubEventsAsync(webSocket, cancellationToken);
        var receiveTask = ReceiveClientMessagesAsync(webSocket, connectionId, cancellationToken);

        try
        {
            await Task.WhenAny(eventTask, receiveTask);
        }
        catch (OperationCanceledException)
        {
            // Normal disconnect
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            if (webSocket.State == WebSocketState.Open)
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        }
    }

    /// <summary>
    /// Broadcast a span event to all connected debug clients.
    /// </summary>
    public void BroadcastSpanEvent(string eventType, object payload)
    {
        var message = JsonSerializer.Serialize(new
        {
            type = eventType,
            data = payload,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        var bytes = Encoding.UTF8.GetBytes(message);
        var segment = new ArraySegment<byte>(bytes);

        foreach (var connection in _connections.Values)
            if (connection.State == WebSocketState.Open)
                try
                {
                    connection.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch
                {
                    // Silently ignore send errors
                }
    }

    private async Task ForwardEventHubEventsAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        await foreach (var envelope in _eventHub.Subscribe(cancellationToken))
        {
            if (webSocket.State != WebSocketState.Open) break;

            var payload = new
            {
                type = $"event.{envelope.Type}",
                data = envelope,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);

            try
            {
                await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                    cancellationToken);
            }
            catch
            {
                break;
            }
        }
    }

    private async Task ReceiveClientMessagesAsync(WebSocket webSocket, string connectionId,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            }
            catch
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close) break;

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                HandleClientMessage(json, connectionId);
            }
        }
    }

    private void HandleClientMessage(string json, string connectionId)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ClientMessage>(json);
            if (message is null) return;

            switch (message.Type)
            {
                case "subscribe.topics":
                    // TODO: Implement topic-based subscription filtering
                    break;
                case "simulation.resume":
                    // Handled by SimulationService
                    break;
                case "simulation.step":
                    // Handled by SimulationService
                    break;
                case "simulation.reset":
                    // Handled by SimulationService
                    break;
            }
        }
        catch
        {
            // Ignore malformed client messages
        }
    }
}

internal sealed class ClientMessage
{
    public string Type { get; set; } = "";
    public object? Data { get; set; }
}
