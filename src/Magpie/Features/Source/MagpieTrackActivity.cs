using System.Diagnostics;

namespace Magpie.Features.Source;

/// <summary>META-OTEL-002/003 — track-job ActivitySource (auto-instrumentation is blind after StartTrack returns).</summary>
public static class MagpieTrackActivity
{
    public const string SourceName = "bardie.source.magpie.track";

    public static ActivitySource Source { get; } = new(SourceName);

    /// <summary>
    /// Long-lived track work must not nest under the short StartTrack RPC span.
    /// Capture <see cref="Activity.Current"/> before <c>Task.Run</c>, then start a linked root.
    /// </summary>
    public static Activity? StartLinked(
        string name,
        ActivityContext linkContext,
        ActivityKind kind = ActivityKind.Internal)
    {
        IEnumerable<ActivityLink>? links = null;
        if (linkContext != default)
        {
            links = [new ActivityLink(linkContext)];
        }

        return Source.StartActivity(name, kind, parentContext: default, tags: null, links: links);
    }

    public static ActivityContext CaptureLinkContext() =>
        Activity.Current?.Context ?? default;
}
