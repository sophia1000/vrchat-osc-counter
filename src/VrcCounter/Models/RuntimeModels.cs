namespace VrcCounter.Models;

public sealed class CounterRuntime
{
    public bool High { get; set; }
    public long LastTriggerMs { get; set; }
}

public sealed record EventPoint(long T, long V);
public sealed record EventSeries(
    string Name,
    IReadOnlyList<EventPoint> Points,
    long Max,
    long EventCount,
    long BucketMs,
    long? DataStartMs = null,
    long? DataEndMs = null,
    long? CurrentValue = null);

public sealed record TimelineBounds(long StartMs, long EndMs, long EventCount);
public sealed record TimelineViewport(long StartMs, long EndMs);

public sealed record GraphTimeline(
    IReadOnlyList<EventSeries> Series,
    TimelineBounds? DataBounds,
    TimelineViewport Viewport,
    long ServerNowMs,
    IReadOnlyList<string> SelectedCounters,
    string Preset,
    string Bucket,
    string Mode,
    bool Follow,
    int MaxPointsPerSeries)
{
    public bool HasData => DataBounds is not null;
}
