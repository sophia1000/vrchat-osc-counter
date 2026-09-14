using Microsoft.Data.Sqlite;
using VrcCounter.Models;

namespace VrcCounter.Services;

public sealed class EventRepository : IAsyncDisposable
{
    public const int MaxSeriesPoints = 4_000;
    public const int MaxReturnedSeriesPoints = MaxSeriesPoints + 2;
    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public EventRepository(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
    }

    public async Task InitializeAsync()
    {
        await _db.OpenAsync();
        await ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000; " +
                           "CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT,counter TEXT NOT NULL,ts_ms INTEGER NOT NULL,count_after INTEGER NOT NULL); " +
                           "CREATE INDEX IF NOT EXISTS idx_events_counter_ts ON events(counter,ts_ms);");
    }

    private async Task ExecuteAsync(string sql)
    {
        await _lock.WaitAsync();
        try { await using var cmd = _db.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
        finally { _lock.Release(); }
    }

    public async Task AddAsync(string counter, long countAfter, long timestampMs)
    {
        await _lock.WaitAsync();
        try
        {
            await using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO events(counter,ts_ms,count_after) VALUES($counter,$ts,$count)";
            cmd.Parameters.AddWithValue("$counter", counter); cmd.Parameters.AddWithValue("$ts", timestampMs); cmd.Parameters.AddWithValue("$count", countAfter);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task<EventRange?> GetRangeAsync(IReadOnlyCollection<string> names)
    {
        var ranges = await GetRangesAsync(names);
        if (ranges.Count == 0) return null;
        return new EventRange(ranges.Values.Min(x => x.StartMs), ranges.Values.Max(x => x.EndMs), ranges.Values.Sum(x => x.EventCount));
    }

    public async Task<IReadOnlyDictionary<string, EventRange>> GetRangesAsync(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0) return new Dictionary<string, EventRange>();
        await _lock.WaitAsync();
        try
        {
            await using var cmd = _db.CreateCommand();
            var parameters = names.Distinct(StringComparer.Ordinal).Select((name, index) => (name, key: $"$counter{index}")).ToArray();
            cmd.CommandText = $"SELECT counter,MIN(ts_ms),MAX(ts_ms),COUNT(*) FROM events WHERE counter IN ({string.Join(',', parameters.Select(x => x.key))}) GROUP BY counter";
            foreach (var item in parameters) cmd.Parameters.AddWithValue(item.key, item.name);
            var ranges = new Dictionary<string, EventRange>(StringComparer.Ordinal);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                ranges[reader.GetString(0)] = new EventRange(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
            return ranges;
        }
        finally { _lock.Release(); }
    }

    public async Task<EventSeries> GetSeriesAsync(string name, long startMs, long endMs, string bucket, string mode, long currentCount, EventRange? dataRange = null)
    {
        var rows = new List<(long Ts, long Count)>();
        var forceRaw = bucket == "raw";
        var requestedBucketMs = bucket switch
        {
            "second" => 1_000L,
            "30seconds" => 30_000L,
            "minute" => 60_000L,
            "hour" => 3_600_000L,
            "day" => 86_400_000L,
            _ => 0L
        };
        var rangeMs = endMs > startMs ? endMs - startMs : 1;
        long eventCount;
        long? before = null;
        long effectiveBucketMs = 0;
        await _lock.WaitAsync();
        try
        {
            await using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM events WHERE counter=$counter AND ts_ms BETWEEN $start AND $end";
                cmd.Parameters.AddWithValue("$counter", name); cmd.Parameters.AddWithValue("$start", startMs); cmd.Parameters.AddWithValue("$end", endMs);
                eventCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }
            await using (var cmd = _db.CreateCommand())
            {
                if (mode == "delta" && forceRaw)
                {
                    effectiveBucketMs = 0;
                    cmd.CommandText = "SELECT ts_ms,COUNT(*) FROM events WHERE counter=$counter AND ts_ms BETWEEN $start AND $end GROUP BY ts_ms ORDER BY ts_ms";
                }
                else if (mode == "delta" || requestedBucketMs > 0 || (!forceRaw && eventCount > MaxSeriesPoints))
                {
                    var safetyBucketMs = (rangeMs + MaxSeriesPoints - 1) / MaxSeriesPoints;
                    effectiveBucketMs = bucket == "auto"
                        ? NiceAutoBucketMs(safetyBucketMs)
                        : requestedBucketMs;
                    if (mode == "delta")
                        cmd.CommandText = "SELECT CAST((ts_ms-$start)/$size AS INTEGER)*$size+$start AS bucket_start,COUNT(*) FROM events WHERE counter=$counter AND ts_ms BETWEEN $start AND $end GROUP BY CAST((ts_ms-$start)/$size AS INTEGER) ORDER BY bucket_start";
                    else
                        cmd.CommandText = "SELECT ts_ms,count_after FROM (SELECT ts_ms,count_after,ROW_NUMBER() OVER(PARTITION BY CAST((ts_ms-$start)/$size AS INTEGER) ORDER BY ts_ms DESC,id DESC) AS rn FROM events WHERE counter=$counter AND ts_ms BETWEEN $start AND $end) WHERE rn=1 ORDER BY ts_ms";
                    cmd.Parameters.AddWithValue("$size", effectiveBucketMs);
                }
                else
                    cmd.CommandText = "SELECT ts_ms,count_after FROM events WHERE counter=$counter AND ts_ms BETWEEN $start AND $end ORDER BY ts_ms ASC,id ASC";
                cmd.Parameters.AddWithValue("$counter", name); cmd.Parameters.AddWithValue("$start", startMs); cmd.Parameters.AddWithValue("$end", endMs);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }
            await using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT count_after FROM events WHERE counter=$counter AND ts_ms<$start ORDER BY ts_ms DESC,id DESC LIMIT 1";
                cmd.Parameters.AddWithValue("$counter", name); cmd.Parameters.AddWithValue("$start", startMs);
                var value = await cmd.ExecuteScalarAsync(); if (value is not null and not DBNull) before = Convert.ToInt64(value);
            }
        }
        finally { _lock.Release(); }

        var points = new List<EventPoint>(); long max = 0;
        if (mode == "delta")
        {
            if (effectiveBucketMs == 0)
            {
                foreach (var row in rows) { points.Add(new(row.Ts, row.Count)); max = Math.Max(max, row.Count); }
            }
            else
            {
                var buckets = rows.ToDictionary(x => x.Ts, x => x.Count);
                var slots = rangeMs / effectiveBucketMs + 1;
                if (slots <= MaxReturnedSeriesPoints)
                {
                    for (var t = startMs; t <= endMs; t += effectiveBucketMs)
                    {
                        var value = buckets.GetValueOrDefault(t); points.Add(new(t, value)); max = Math.Max(max, value);
                        if (t > long.MaxValue - effectiveBucketMs) break;
                    }
                }
                else
                {
                    void AddSparse(long t, long value)
                    {
                        if (points.Count > 0 && points[^1].T == t) points[^1] = new(t, value);
                        else points.Add(new(t, value));
                        max = Math.Max(max, value);
                    }

                    long? previous = null;
                    foreach (var row in rows)
                    {
                        if (!previous.HasValue && row.Ts > startMs) AddSparse(startMs, 0);
                        if (previous.HasValue && row.Ts > previous.Value + effectiveBucketMs)
                        {
                            AddSparse(previous.Value + effectiveBucketMs, 0);
                            if (row.Ts - effectiveBucketMs > previous.Value + effectiveBucketMs) AddSparse(row.Ts - effectiveBucketMs, 0);
                        }
                        AddSparse(row.Ts, row.Count);
                        previous = row.Ts;
                    }
                    if (!previous.HasValue) AddSparse(startMs, 0);
                    if (previous.HasValue && previous.Value < endMs) AddSparse(Math.Min(endMs, previous.Value + effectiveBucketMs), 0);
                    if (points[^1].T < endMs) AddSparse(endMs, 0);
                }
            }
        }
        else
        {
            long? last = before;
            if (before.HasValue) { points.Add(new(startMs, before.Value)); max = before.Value; }
            foreach (var row in rows) { last = row.Count; points.Add(new(row.Ts, row.Count)); max = Math.Max(max, row.Count); }
            if (last.HasValue && (points.Count == 0 || points[^1].T < endMs)) points.Add(new(endMs, last.Value));
        }
        return new(name, points, max, eventCount, effectiveBucketMs, dataRange?.StartMs, dataRange?.EndMs, currentCount);
    }

    private static long NiceAutoBucketMs(long minimumMs)
    {
        long[] steps = [1_000, 5_000, 10_000, 30_000, 60_000, 5 * 60_000, 15 * 60_000, 30 * 60_000,
            3_600_000, 6 * 3_600_000, 12 * 3_600_000, 86_400_000, 7 * 86_400_000, 30 * 86_400_000L];
        foreach (var step in steps) if (step >= minimumMs) return step;
        var month = 30 * 86_400_000L;
        return ((minimumMs + month - 1) / month) * month;
    }

    public async Task<GraphTimeline> GetTimelineAsync(
        IReadOnlyList<string> names,
        IReadOnlyDictionary<string, long> currentCounts,
        long? requestedStartMs,
        long? requestedEndMs,
        string preset,
        string bucket,
        string mode,
        bool follow,
        long serverNowMs,
        long? customStartMs = null,
        long? customEndMs = null)
    {
        var selected = names.Distinct(StringComparer.Ordinal).ToArray();
        var ranges = await GetRangesAsync(selected);
        var combined = ranges.Count == 0
            ? null
            : new EventRange(ranges.Values.Min(x => x.StartMs), ranges.Values.Max(x => x.EndMs), ranges.Values.Sum(x => x.EventCount));
        var viewport = TimelineViewportResolver.Resolve(combined, requestedStartMs, requestedEndMs, preset, customStartMs, customEndMs, serverNowMs);
        var series = new List<EventSeries>(selected.Length);
        foreach (var name in selected)
        {
            ranges.TryGetValue(name, out var range);
            series.Add(await GetSeriesAsync(name, viewport.StartMs, viewport.EndMs, bucket, mode, currentCounts.GetValueOrDefault(name), range));
        }
        return new(
            series,
            combined is null ? null : new TimelineBounds(combined.StartMs, combined.EndMs, combined.EventCount),
            viewport,
            serverNowMs,
            selected,
            preset,
            bucket,
            mode,
            follow,
            MaxReturnedSeriesPoints);
    }

    public async ValueTask DisposeAsync() { await _db.DisposeAsync(); _lock.Dispose(); }
}

public sealed record EventRange(long StartMs, long EndMs, long EventCount);

public static class TimelineViewportResolver
{
    private static readonly IReadOnlyDictionary<string, long> PresetSpans = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["10m"] = 10 * 60_000L,
        ["30m"] = 30 * 60_000L,
        ["1h"] = 60 * 60_000L,
        ["2h"] = 2 * 60 * 60_000L,
        ["5h"] = 5 * 60 * 60_000L,
        ["12h"] = 12 * 60 * 60_000L,
        ["1d"] = 24 * 60 * 60_000L,
        ["7d"] = 7 * 24 * 60 * 60_000L,
        ["30d"] = 30 * 24 * 60 * 60_000L
    };

    public static TimelineViewport Resolve(
        EventRange? bounds,
        long? requestedStartMs,
        long? requestedEndMs,
        string preset,
        long? customStartMs,
        long? customEndMs,
        long serverNowMs)
    {
        if (bounds is null) return new(serverNowMs, serverNowMs);

        long start;
        long end;
        if (requestedStartMs.HasValue || requestedEndMs.HasValue)
        {
            start = requestedStartMs ?? bounds.StartMs;
            end = requestedEndMs ?? bounds.EndMs;
        }
        else if (preset == "custom" && (customStartMs.HasValue || customEndMs.HasValue))
        {
            start = customStartMs ?? bounds.StartMs;
            end = customEndMs ?? bounds.EndMs;
        }
        else if (PresetSpans.TryGetValue(preset, out var span))
        {
            end = bounds.EndMs;
            start = end >= long.MinValue + span ? end - span : long.MinValue;
        }
        else
        {
            start = bounds.StartMs;
            end = bounds.EndMs;
        }

        if (end < start) (start, end) = (end, start);
        start = Math.Clamp(start, bounds.StartMs, bounds.EndMs);
        end = Math.Clamp(end, bounds.StartMs, bounds.EndMs);
        return new(start, end);
    }
}
