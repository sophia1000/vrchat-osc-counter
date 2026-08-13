namespace VrcCounter.Models;

public sealed class CounterRuntime
{
    public bool High { get; set; }
    public long LastTriggerMs { get; set; }
}

public sealed record EventPoint(long T, long V);
public sealed record EventSeries(string Name, IReadOnlyList<EventPoint> Points, long Max, long EventCount, long BucketMs);
