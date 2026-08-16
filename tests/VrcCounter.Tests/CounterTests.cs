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
    public void ConfigMigration_DefaultsMissingOrInvalidOscTransportToOscQuery()
    {
        var missing = JsonSerializer.Deserialize<AppConfig>("{}", JsonOptions.Default)!;
        missing.Normalize();
        Assert.Equal(AppConfig.CurrentConfigVersion, missing.ConfigVersion);
        Assert.Equal(AppConfig.OscQueryTransport, missing.OscTransport);

        var invalid = JsonSerializer.Deserialize<AppConfig>("{\"osc_transport\":\"both\"}", JsonOptions.Default)!;
        invalid.Normalize();
        Assert.Equal(AppConfig.OscQueryTransport, invalid.OscTransport);

        var legacyAlias = JsonSerializer.Deserialize<AppConfig>("{\"osc_transport\":\"normal\"}", JsonOptions.Default)!;
        legacyAlias.Normalize();
        Assert.Equal(AppConfig.LegacyOscTransport, legacyAlias.OscTransport);
    }

    [Fact]
    public async Task LegacyOscTransport_DoesNotStartOscQuery()
    {
        var cfg = EmptyConfig();
        cfg.OscTransport = AppConfig.LegacyOscTransport;
        cfg.OscInPort = 0;
        await using var fixture = await Fixture.CreateAsync(cfg);

        await fixture.State.Osc.RestartAsync();

        Assert.Equal(AppConfig.LegacyOscTransport, fixture.State.Osc.SelectedTransport);
        Assert.True(fixture.State.Osc.TransportRunning);
        Assert.True(fixture.State.Osc.LegacyOscRunning);
        Assert.False(fixture.State.Osc.OscQueryRunning);
        Assert.Null(fixture.State.Osc.OscQueryTcpPort);
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
    public async Task ChatboxTrigger_SendsVrchatPacketAndReportsDeliveryToUdp()
    {
        using var receiver = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var cfg = EmptyConfig(); var c = CounterConfig.Create("Chat", "/chat");
        c.DebounceMs = 0; c.SendChatbox = true; c.ChatboxNotify = false; c.ChatboxTemplate = "🎀 Count {count}";
        cfg.Counters[c.Name] = c; cfg.CounterOrder.Add(c.Name);
        cfg.OscOutPort = ((System.Net.IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        cfg.ChatboxMinIntervalMs = 0; cfg.ChatboxAutoClearMs = 0;
        await using var fixture = await Fixture.CreateAsync(cfg);

        await fixture.State.HandleOscAsync("/chat", [.6f]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var datagram = await receiver.ReceiveAsync(timeout.Token);
        var message = Assert.Single(OscCodec.Decode(datagram.Buffer));
        var pythonOscPacket = Convert.FromHexString("2F63686174626F782F696E70757400002C73544600000000F09F8E8020436F756E74203100000000");

        Assert.Equal(pythonOscPacket, datagram.Buffer);
        Assert.Equal("/chatbox/input", message.Address);
        Assert.Equal(["🎀 Count 1", true, false], message.Values);
        var status = fixture.State.Osc.GetSendStatus();
        Assert.Equal(1, status.SentPacketCount);
        Assert.Equal("/chatbox/input", status.LastSendAddress);
        Assert.Empty(status.LastSendError);
    }

    [Fact]
    public async Task ChatboxLimiter_AppliesOneSecondFloorToEverySendPath()
    {
        using var receiver = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var cfg = EmptyConfig();
        cfg.OscOutPort = ((System.Net.IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        cfg.ChatboxMinIntervalMs = 0;
        cfg.ChatboxPerMinuteLimit = 100;
        await using var fixture = await Fixture.CreateAsync(cfg);

        Assert.True(await fixture.State.Chatbox.SendTestAsync());
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await fixture.State.Chatbox.SendTestAsync());
        timer.Stop();

        Assert.InRange(timer.ElapsedMilliseconds, 900, 5000);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await receiver.ReceiveAsync(timeout.Token);
        await receiver.ReceiveAsync(timeout.Token);
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

    [Fact]
    public async Task EventRepository_ReportsCombinedAndPerCounterDataBounds()
    {
        await using var fixture = await Fixture.CreateAsync(EmptyConfig());
        await fixture.Repository.AddAsync("A", 1, 1_000);
        await fixture.Repository.AddAsync("A", 2, 2_000);
        await fixture.Repository.AddAsync("B", 7, 500);
        await fixture.Repository.AddAsync("B", 8, 4_000);

        var ranges = await fixture.Repository.GetRangesAsync(["A", "B", "Missing"]);
        Assert.Equal(new EventRange(1_000, 2_000, 2), ranges["A"]);
        Assert.Equal(new EventRange(500, 4_000, 2), ranges["B"]);
        Assert.False(ranges.ContainsKey("Missing"));
        Assert.Equal(new EventRange(500, 4_000, 4), await fixture.Repository.GetRangeAsync(["A", "B", "Missing"]));
    }

    [Fact]
    public async Task Timeline_UsesPresetAsInitialViewportAndKeepsFullZoomBounds()
    {
        await using var fixture = await Fixture.CreateAsync(EmptyConfig());
        await fixture.Repository.AddAsync("A", 1, 1_000);
        await fixture.Repository.AddAsync("A", 2, 7_201_000);

        var timeline = await fixture.Repository.GetTimelineAsync(
            ["A"], new Dictionary<string, long> { ["A"] = 2 }, null, null,
            "1h", "auto", "total", true, 9_000_000);

        Assert.Equal(new TimelineBounds(1_000, 7_201_000, 2), timeline.DataBounds);
        Assert.Equal(new TimelineViewport(3_601_000, 7_201_000), timeline.Viewport);
        Assert.True(timeline.Follow);
        Assert.Equal(9_000_000, timeline.ServerNowMs);
        Assert.Equal(1_000, timeline.Series[0].DataStartMs);
        Assert.Equal(7_201_000, timeline.Series[0].DataEndMs);
    }

    [Fact]
    public async Task Timeline_ClampsUserViewportToRecordedDataAndDoesNotInventMissingHistory()
    {
        await using var fixture = await Fixture.CreateAsync(EmptyConfig());
        await fixture.Repository.AddAsync("A", 4, 1_000);
        await fixture.Repository.AddAsync("A", 5, 2_000);

        var timeline = await fixture.Repository.GetTimelineAsync(
            ["A", "Missing"], new Dictionary<string, long> { ["A"] = 5, ["Missing"] = 999 },
            -10_000, 50_000, "all", "auto", "total", false, 60_000);

        Assert.Equal(new TimelineViewport(1_000, 2_000), timeline.Viewport);
        Assert.Equal(["A", "Missing"], timeline.SelectedCounters);
        Assert.Equal(2, timeline.Series.Count);
        Assert.Empty(timeline.Series.Single(x => x.Name == "Missing").Points);
        Assert.Null(timeline.Series.Single(x => x.Name == "Missing").DataStartMs);
        Assert.Equal(999, timeline.Series.Single(x => x.Name == "Missing").CurrentValue);
    }

    [Fact]
    public void TimelineViewportResolver_NormalizesAndClampsExplicitSelection()
    {
        var bounds = new EventRange(1_000, 5_000, 8);
        Assert.Equal(new TimelineViewport(1_000, 5_000), TimelineViewportResolver.Resolve(bounds, 9_000, -2_000, "1h", null, null, 10_000));
        Assert.Equal(new TimelineViewport(2_000, 4_000), TimelineViewportResolver.Resolve(bounds, null, null, "custom", 4_000, 2_000, 10_000));
        Assert.Equal(new TimelineViewport(10_000, 10_000), TimelineViewportResolver.Resolve(null, null, null, "all", null, null, 10_000));
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
