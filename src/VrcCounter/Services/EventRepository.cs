using Microsoft.Data.Sqlite;
using VrcCounter.Models;

namespace VrcCounter.Services;

public sealed class EventRepository : IAsyncDisposable
{
    private const int MaxSeriesPoints = 4_000;
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
        if (names.Count == 0) return null;
        await _lock.WaitAsync();
        try
        {
            await using var cmd = _db.CreateCommand();
            var parameters = names.Select((name, index) => (name, key: $"$counter{index}")).ToArray();
            cmd.CommandText = $"SELECT MIN(ts_ms),MAX(ts_ms),COUNT(*) FROM events WHERE counter IN ({string.Join(',', parameters.Select(x => x.key))})";
            foreach (var item in parameters) cmd.Parameters.AddWithValue(item.key, item.name);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || reader.IsDBNull(0)) return null;
            return new EventRange(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }
        finally { _lock.Release(); }
    }

    public async Task<EventSeries> GetSeriesAsync(string name, long startMs, long endMs, string bucket, string mode, long currentCount)
    {
        var rows = new List<(long Ts, long Count)>();
        var requestedBucketMs = bucket == "minute" ? 60_000L : bucket == "day" ? 86_400_000L : 3_600_000L;
        var rangeMs = Math.Max(1, endMs - startMs);
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
                if (mode == "delta" || eventCount > MaxSeriesPoints)
                {
                    effectiveBucketMs = Math.Max(requestedBucketMs, (rangeMs + MaxSeriesPoints - 1) / MaxSeriesPoints);
                    if (mode == "delta")
                        cmd.CommandText = "SELECT CAST(ts_ms/$size AS INTEGER)*$size AS bucket_start,COUNT(*) FROM events WHERE counter=$counter AND ts_ms BETWEEN $start AND $end GROUP BY CAST(ts_ms/$size AS INTEGER) ORDER BY bucket_start";
                    else
                        cmd.CommandText = "SELECT ts_ms,count_after FROM (SELECT ts_ms,count_after,ROW_NUMBER() OVER(PARTITION BY CAST(ts_ms/$size AS INTEGER) ORDER BY ts_ms DESC,id DESC) AS rn FROM events WHERE counter=$counter AND ts_ms BETWEEN $start AND $end) WHERE rn=1 ORDER BY ts_ms";
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
                cmd.CommandText = "SELECT count_after FROM events WHERE counter=$counter AND ts_ms<=$start ORDER BY ts_ms DESC LIMIT 1";
                cmd.Parameters.AddWithValue("$counter", name); cmd.Parameters.AddWithValue("$start", startMs);
                var value = await cmd.ExecuteScalarAsync(); if (value is not null and not DBNull) before = Convert.ToInt64(value);
            }
        }
        finally { _lock.Release(); }

        var points = new List<EventPoint>(); long max = 0;
        if (mode == "delta")
        {
            var buckets = rows.ToDictionary(x => x.Ts, x => x.Count);
            for (var t = startMs / effectiveBucketMs * effectiveBucketMs; t <= endMs; t += effectiveBucketMs)
            {
                var value = buckets.GetValueOrDefault(t); points.Add(new(t, value)); max = Math.Max(max, value);
                if (t > long.MaxValue - effectiveBucketMs) break;
            }
        }
        else
        {
            var baseline = before ?? (rows.Count > 0 ? rows[0].Count : currentCount);
            points.Add(new(startMs, baseline)); max = baseline; var last = baseline;
            foreach (var row in rows) { last = row.Count; points.Add(new(row.Ts, last)); max = Math.Max(max, last); }
            points.Add(new(endMs, last));
        }
        return new(name, points, max, eventCount, effectiveBucketMs);
    }

    public async ValueTask DisposeAsync() { await _db.DisposeAsync(); _lock.Dispose(); }
}

public sealed record EventRange(long StartMs, long EndMs, long EventCount);
