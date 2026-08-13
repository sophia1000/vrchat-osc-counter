using System.Text.Json;
using VrcCounter.Models;
using VrcCounter.Services;

namespace VrcCounter.Tests;

public sealed class CounterTests
{
    [Fact]
    public void OscCodec_RoundTrips_VrchatTypesAndUtf8()
    {
        var packet = OscCodec.Encode("/chatbox/input", "🎀 hello", true, false, 42, 1.25f);
        var message = Assert.Single(OscCodec.Decode(packet));
        Assert.Equal("/chatbox/input", message.Address);
        Assert.Equal("🎀 hello", message.Values[0]); Assert.Equal(true, message.Values[1]); Assert.Equal(false, message.Values[2]);
        Assert.Equal(42, message.Values[3]); Assert.Equal(1.25f, message.Values[4]);
    }

    [Fact]
    public async Task ConfigStore_LoadsSuppliedModernSchemaWithoutDroppingFeatures()
    {
        var source = FindWorkspaceFile("vrc_multi_param_counter.config.json"); var dir = NewTempDir(); var path = Path.Combine(dir, Path.GetFileName(source)); File.Copy(source, path);
        try
        {
            var store = new ConfigStore(path); var cfg = await store.LoadAsync();
            Assert.Equal(10, cfg.Counters.Count); Assert.Equal(4, cfg.Graphs.Count); Assert.Equal(10, cfg.CounterOrder.Count);
            Assert.All(cfg.Counters.Values, c => Assert.Contains(c.TriggerMode, new[] { "threshold", "int_eq" }));
            await store.FlushAsync(cfg); var loadedAgain = await store.LoadAsync(); Assert.Equal(cfg.GraphOrder, loadedAgain.GraphOrder); Assert.Equal(cfg.Counters.Keys, loadedAgain.Counters.Keys);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ThresholdMode_UsesStrictHysteresisAndSharedAddress()
    {
        var cfg = EmptyConfig();
        cfg.Counters["A"] = CounterConfig.Create("A", "/same"); cfg.Counters["B"] = CounterConfig.Create("B", "/same"); cfg.CounterOrder.AddRange(["A", "B"]);
        cfg.Counters["A"].DebounceMs = 0; cfg.Counters["B"].DebounceMs = 0; cfg.Counters["A"].SendChatbox = false; cfg.Counters["B"].SendChatbox = false;
        await using var fixture = await Fixture.CreateAsync(cfg);
        await fixture.State.HandleOscAsync("/same", [.5f]); Assert.Equal(0, fixture.State.Read(c => c.Counters["A"].Count));
        await fixture.State.HandleOscAsync("/same", [.6f]); Assert.Equal(1, fixture.State.Read(c => c.Counters["A"].Count)); Assert.Equal(1, fixture.State.Read(c => c.Counters["B"].Count));
        await fixture.State.HandleOscAsync("/same", [.7f]); Assert.Equal(1, fixture.State.Read(c => c.Counters["A"].Count));
        await fixture.State.HandleOscAsync("/same", [.1f]); await fixture.State.HandleOscAsync("/same", [.6f]); Assert.Equal(1, fixture.State.Read(c => c.Counters["A"].Count));
        await fixture.State.HandleOscAsync("/same", [.09f]); await fixture.State.HandleOscAsync("/same", [.6f]); Assert.Equal(2, fixture.State.Read(c => c.Counters["A"].Count));
    }

    [Fact]
    public async Task IntegerMode_RoundsLikePythonAndDebouncesWithoutHysteresis()
    {
        var cfg = EmptyConfig(); var c = CounterConfig.Create("Int", "/int"); c.TriggerMode = "int_eq"; c.IntValue = 2; c.DebounceMs = 0; c.SendChatbox = false; cfg.Counters[c.Name] = c; cfg.CounterOrder.Add(c.Name);
        await using var fixture = await Fixture.CreateAsync(cfg);
        await fixture.State.HandleOscAsync("/int", [1.5f]); await fixture.State.HandleOscAsync("/int", [2]);
        Assert.Equal(2, fixture.State.Read(x => x.Counters["Int"].Count));
    }

    [Fact]
    public void Templates_KeepUnknownFieldsAndFormatCounts()
        => Assert.Equal("Boops: 12,345 {unknown}", AppState.FormatTemplate("{name}: {count} {unknown}", "Boops", 12345));

    [Fact]
    public async Task EventRepository_ReturnsCompleteRecordedRangeAndTimeSeries()
    {
        var cfg = EmptyConfig();
        await using var fixture = await Fixture.CreateAsync(cfg);
        await fixture.Repository.AddAsync("History", 10, 1_000);
        await fixture.Repository.AddAsync("History", 11, 2_000);
        await fixture.Repository.AddAsync("History", 12, 3_000);

        var range = await fixture.Repository.GetRangeAsync(["History"]);
        Assert.Equal(new EventRange(1_000, 3_000, 3), range);

        var series = await fixture.Repository.GetSeriesAsync("History", range!.StartMs, range.EndMs, "minute", "total", 99);
        Assert.Equal(3, series.EventCount);
        Assert.Contains(series.Points, p => p.T == 1_000 && p.V == 10);
        Assert.Contains(series.Points, p => p.T == 2_000 && p.V == 11);
        Assert.Contains(series.Points, p => p.T == 3_000 && p.V == 12);
    }

    [Fact]
    public async Task EventRepository_DownsamplesLargeTotalSeries()
    {
        var cfg = EmptyConfig();
        await using var fixture = await Fixture.CreateAsync(cfg);
        for (var i = 0; i < 5_000; i++) await fixture.Repository.AddAsync("Large", i + 1, i * 60_000L);

        var series = await fixture.Repository.GetSeriesAsync("Large", 0, 5_000L * 60_000L, "minute", "total", 5_000);
        Assert.Equal(5_000, series.EventCount);
        Assert.InRange(series.Points.Count, 2, 4_002);
        Assert.True(series.BucketMs >= 60_000);
        Assert.Equal(5_000, series.Points[^1].V);
    }

    private static AppConfig EmptyConfig() => new() { Counters = [], CounterOrder = [], Graphs = new() { ["g1"] = GraphConfig.Create("Test") }, GraphOrder = ["g1"] };
    private static string NewTempDir() { var path = Path.Combine(Path.GetTempPath(), "VrcCounterTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static string FindWorkspaceFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory); while (dir is not null) { var path = Path.Combine(dir.FullName, name); if (File.Exists(path)) return path; dir = dir.Parent; }
        throw new FileNotFoundException(name);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; init; } = ""; public AppState State { get; init; } = null!; public EventRepository Repository { get; init; } = null!;
        public static async Task<Fixture> CreateAsync(AppConfig cfg)
        {
            var dir = NewTempDir(); var store = new ConfigStore(Path.Combine(dir, "config.json")); var repo = new EventRepository(Path.Combine(dir, "events.sqlite3")); await repo.InitializeAsync(); return new() { DirectoryPath = dir, Repository = repo, State = new AppState(cfg, store, repo) };
        }
        public async ValueTask DisposeAsync() { await State.Osc.DisposeAsync(); await Repository.DisposeAsync(); Directory.Delete(DirectoryPath, true); }
    }
}
