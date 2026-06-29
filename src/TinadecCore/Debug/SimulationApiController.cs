using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TinadecCore.Storage;
using TinadecCore.Tracing;

namespace TinadecCore.Debug;

/// <summary>
/// REST API endpoints for the Agent Debug Studio simulation features.
/// Supports message injection, model response mocking, tool result injection,
/// approval forcing, state patching, and breakpoint management.
/// </summary>
public static class SimulationApiController
{
    public static IEndpointRouteBuilder MapSimulationApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/debug");

        // Simulation endpoints
        group.MapPost("/simulate/message", SimulateMessage);
        group.MapPost("/simulate/model-response", SimulateModelResponse);
        group.MapPost("/simulate/tool-result", SimulateToolResult);
        group.MapPost("/simulate/approval-decision", ForceApprovalDecision);
        group.MapPost("/simulate/state-patch", PatchAgentState);

        // Breakpoint endpoints
        group.MapPost("/breakpoints", CreateBreakpoint);
        group.MapGet("/breakpoints", ListBreakpoints);
        group.MapDelete("/breakpoints/{id}", DeleteBreakpoint);

        return endpoints;
    }

    private static IResult SimulateMessage(
        SimulateMessageRequest request,
        SimulationService simulation,
        AgentTracing tracing)
    {
        if (!tracing.Options.Enabled)
            return Results.BadRequest(new { code = "TRACING_DISABLED", message = "Tracing is not enabled." });

        var result = simulation.InjectMessage(request);
        return Results.Ok(result);
    }

    private static IResult SimulateModelResponse(
        SimulateModelResponseRequest request,
        SimulationService simulation,
        AgentTracing tracing)
    {
        if (!tracing.Options.Enabled)
            return Results.BadRequest(new { code = "TRACING_DISABLED", message = "Tracing is not enabled." });

        var result = simulation.InjectModelResponse(request);
        return Results.Ok(result);
    }

    private static IResult SimulateToolResult(
        SimulateToolResultRequest request,
        SimulationService simulation,
        AgentTracing tracing)
    {
        if (!tracing.Options.Enabled)
            return Results.BadRequest(new { code = "TRACING_DISABLED", message = "Tracing is not enabled." });

        var result = simulation.InjectToolResult(request);
        return Results.Ok(result);
    }

    private static IResult ForceApprovalDecision(
        ForceApprovalDecisionRequest request,
        SimulationService simulation,
        CoreStore store,
        Services.EventHub events,
        AgentTracing tracing)
    {
        if (!tracing.Options.Enabled)
            return Results.BadRequest(new { code = "TRACING_DISABLED", message = "Tracing is not enabled." });

        var result = simulation.ForceApprovalDecision(request);
        return Results.Ok(result);
    }

    private static IResult PatchAgentState(
        PatchAgentStateRequest request,
        SimulationService simulation,
        AgentTracing tracing)
    {
        if (!tracing.Options.Enabled)
            return Results.BadRequest(new { code = "TRACING_DISABLED", message = "Tracing is not enabled." });

        var result = simulation.PatchAgentState(request);
        return Results.Ok(result);
    }

    private static IResult CreateBreakpoint(
        CreateBreakpointRequest request,
        BreakpointService breakpoints)
    {
        var breakpoint = breakpoints.Create(request);
        return Results.Created($"/api/v1/debug/breakpoints/{breakpoint.Id}", breakpoint);
    }

    private static IResult ListBreakpoints(BreakpointService breakpoints)
    {
        return Results.Ok(breakpoints.ListAll());
    }

    private static IResult DeleteBreakpoint(string id, BreakpointService breakpoints)
    {
        var deleted = breakpoints.Delete(id);
        return deleted
            ? Results.NoContent()
            : Results.NotFound(new { code = "BREAKPOINT_NOT_FOUND", message = $"Breakpoint '{id}' was not found." });
    }
}

// --- Simulation Request/Response Models ---

public sealed class SimulateMessageRequest
{
    public string SessionId { get; set; } = "";
    public string Content { get; set; } = "";
    public bool SkipModelCall { get; set; }
    public string? MockModelResponse { get; set; }
}

public sealed class SimulateMessageResponse
{
    public string SimulationId { get; set; } = "";
    public string TraceId { get; set; } = "";
    public bool Simulated { get; set; }
}

public sealed class SimulateModelResponseRequest
{
    public string SessionId { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class SimulateToolResultRequest
{
    public string RunId { get; set; } = "";
    public string ToolId { get; set; } = "";
    public string Status { get; set; } = "completed";
    public string? Summary { get; set; }
}

public sealed class ForceApprovalDecisionRequest
{
    public string ApprovalId { get; set; } = "";
    public string Decision { get; set; } = "approved";
}

public sealed class PatchAgentStateRequest
{
    public string SessionId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public Dictionary<string, object?> State { get; set; } = new();
}
