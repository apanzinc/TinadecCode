using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TinadecCore.Tracing;

/// <summary>
/// Central ActivitySource for TinadecCore tracing.
/// </summary>
public static class TinadecActivitySource
{
    public const string SourceName = "TinadecCore";
    public static readonly ActivitySource Instance = new(SourceName, "1.0.0");
}

/// <summary>
/// Convenience extension methods for working with Activity (span) objects.
/// </summary>
public static class ActivityExtensions
{
    /// <summary>
    /// Set a span attribute (tag) on the current activity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Activity? SetTag(this Activity? activity, string key, string? value)
    {
        activity?.SetTag(key, value);
        return activity;
    }

    /// <summary>
    /// Set a span attribute (tag) on the current activity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Activity? SetTag(this Activity? activity, string key, int? value)
    {
        activity?.SetTag(key, value);
        return activity;
    }

    /// <summary>
    /// Set a span attribute (tag) on the current activity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Activity? SetTag(this Activity? activity, string key, long? value)
    {
        activity?.SetTag(key, value);
        return activity;
    }

    /// <summary>
    /// Set a span attribute (tag) on the current activity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Activity? SetTag(this Activity? activity, string key, double? value)
    {
        activity?.SetTag(key, value);
        return activity;
    }

    /// <summary>
    /// Set a span attribute (tag) on the current activity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Activity? SetTag(this Activity? activity, string key, bool? value)
    {
        activity?.SetTag(key, value);
        return activity;
    }

    /// <summary>
    /// Add an event to the current activity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Activity? AddSpanEvent(this Activity? activity, string name,
        IEnumerable<KeyValuePair<string, object?>>? attributes = null)
    {
        if (activity is null) return null;
        var tags = attributes is null
            ? new ActivityTagsCollection()
            : new ActivityTagsCollection(attributes);
        activity.AddEvent(new ActivityEvent(name, tags: tags));
        return activity;
    }

    /// <summary>
    /// Mark the current activity as errored.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Activity? SetError(this Activity? activity, string errorMessage)
    {
        if (activity is null) return null;
        activity.SetStatus(ActivityStatusCode.Error, errorMessage);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.message"] = errorMessage,
            ["exception.type"] = "Error"
        }));
        return activity;
    }

    /// <summary>
    /// Start a new child span from the current activity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Activity? StartChildSpan(this Activity? parent, string name,
        ActivityKind kind = ActivityKind.Internal)
    {
        return TinadecActivitySource.Instance.StartActivity(name, kind, parent?.Context ?? default);
    }
}
