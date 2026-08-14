using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcCounter.Models;

public sealed class AppConfig
{
    public const int CurrentConfigVersion = 27;
    public const string OscQueryTransport = "oscquery";
    public const string LegacyOscTransport = "legacy";

    [JsonPropertyName("config_version")] public int ConfigVersion { get; set; } = CurrentConfigVersion;
    [JsonPropertyName("osc_transport")] public string OscTransport { get; set; } = OscQueryTransport;
    [JsonPropertyName("osc_in_ip")] public string OscInIp { get; set; } = "127.0.0.1";
    [JsonPropertyName("osc_in_port")] public int OscInPort { get; set; } = 9001;
    [JsonPropertyName("osc_out_ip")] public string OscOutIp { get; set; } = "127.0.0.1";
    [JsonPropertyName("osc_out_port")] public int OscOutPort { get; set; } = 9000;
    [JsonPropertyName("web_ui_bind")] public string WebUiBind { get; set; } = "127.0.0.1";
    [JsonPropertyName("web_ui_port")] public int WebUiPort { get; set; } = 17801;
    [JsonPropertyName("webview_enabled")] public bool WebviewEnabled { get; set; } = true;
    [JsonPropertyName("webview_title")] public string WebviewTitle { get; set; } = "VRChat Counter";
    [JsonPropertyName("webview_width")] public int WebviewWidth { get; set; } = 1200;
    [JsonPropertyName("webview_height")] public int WebviewHeight { get; set; } = 800;
    [JsonPropertyName("webview_min_width")] public int WebviewMinWidth { get; set; } = 900;
    [JsonPropertyName("webview_min_height")] public int WebviewMinHeight { get; set; } = 600;
    [JsonPropertyName("webview_frameless")] public bool WebviewFrameless { get; set; }
    [JsonPropertyName("webview_easy_drag")] public bool WebviewEasyDrag { get; set; } = true;
    [JsonPropertyName("remember_window_size")] public bool RememberWindowSize { get; set; } = true;
    [JsonPropertyName("last_window_width")] public int? LastWindowWidth { get; set; }
    [JsonPropertyName("last_window_height")] public int? LastWindowHeight { get; set; }
    [JsonPropertyName("save_throttle_ms")] public int SaveThrottleMs { get; set; } = 400;
    [JsonPropertyName("chatbox_mode")] public string ChatboxMode { get; set; } = "modern";
    [JsonPropertyName("chatbox_per_minute_limit")] public int ChatboxPerMinuteLimit { get; set; } = 30;
    [JsonPropertyName("chatbox_min_interval_ms")] public int ChatboxMinIntervalMs { get; set; } = 1200;
    [JsonPropertyName("chatbox_auto_clear_ms")] public int ChatboxAutoClearMs { get; set; } = 10000;
    [JsonPropertyName("chatbox_enabled_by_default")] public bool ChatboxEnabledByDefault { get; set; } = true;
    [JsonPropertyName("chatbox_notify_by_default")] public bool ChatboxNotifyByDefault { get; set; } = true;
    [JsonPropertyName("counters_compact")] public bool CountersCompact { get; set; }
    [JsonPropertyName("home_graphs_columns")] public int HomeGraphsColumns { get; set; } = 2;
    [JsonPropertyName("counters")] public Dictionary<string, CounterConfig> Counters { get; set; } = [];
    [JsonPropertyName("counter_order")] public List<string> CounterOrder { get; set; } = [];
    [JsonPropertyName("graphs")] public Dictionary<string, GraphConfig> Graphs { get; set; } = [];
    [JsonPropertyName("graph_order")] public List<string> GraphOrder { get; set; } = [];
    [JsonPropertyName("home_graph_row_heights")] public List<int> HomeGraphRowHeights { get; set; } = [];

    public static AppConfig CreateDefault()
    {
        var cfg = new AppConfig();
        cfg.Counters["HeadPat"] = CounterConfig.Create("HeadPat", "/avatar/parameters/HeadPat");
        cfg.CounterOrder.Add("HeadPat");
        cfg.Graphs["g1"] = GraphConfig.Create("Home Graph 1");
        cfg.GraphOrder.Add("g1");
        return cfg;
    }

    public void Normalize()
    {
        ConfigVersion = Math.Max(ConfigVersion, CurrentConfigVersion);
        OscTransport = OscTransport?.Trim().ToLowerInvariant() switch
        {
            LegacyOscTransport or "osc" or "normal" or "legacy_osc" => LegacyOscTransport,
            _ => OscQueryTransport
        };
        OscInIp ??= "127.0.0.1"; OscOutIp ??= "127.0.0.1"; WebUiBind ??= "127.0.0.1";
        WebviewTitle ??= "VRChat Counter"; ChatboxMode ??= "modern";
        Counters ??= []; CounterOrder ??= []; Graphs ??= []; GraphOrder ??= []; HomeGraphRowHeights ??= [];
        foreach (var (name, counter) in Counters.ToArray()) { counter.Normalize(name); }
        CounterOrder = CounterOrder.Where(Counters.ContainsKey).Distinct().ToList();
        CounterOrder.AddRange(Counters.Keys.Where(x => !CounterOrder.Contains(x)));
        if (Graphs.Count == 0) { Graphs["g1"] = GraphConfig.Create("Home Graph 1"); }
        foreach (var (id, graph) in Graphs) graph.Normalize(graph.Name ?? $"Home Graph {id}");
        GraphOrder = GraphOrder.Where(Graphs.ContainsKey).Distinct().ToList();
        GraphOrder.AddRange(Graphs.Keys.Where(x => !GraphOrder.Contains(x)));
        HomeGraphsColumns = HomeGraphsColumns is 1 or 2 or 3 ? HomeGraphsColumns : 2;
    }

    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, JsonOptions.Default), JsonOptions.Default)!;
}

public sealed class CounterConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Counter";
    [JsonPropertyName("address")] public string Address { get; set; } = "/avatar/parameters/SomeParam";
    [JsonPropertyName("count")] public long Count { get; set; }
    [JsonPropertyName("trigger_mode")] public string TriggerMode { get; set; } = "threshold";
    [JsonPropertyName("threshold")] public double Threshold { get; set; } = .5;
    [JsonPropertyName("release_threshold")] public double ReleaseThreshold { get; set; } = .1;
    [JsonPropertyName("int_value")] public int IntValue { get; set; } = 1;
    [JsonPropertyName("debounce_ms")] public int DebounceMs { get; set; } = 500;
    [JsonPropertyName("send_chatbox")] public bool SendChatbox { get; set; } = true;
    [JsonPropertyName("chatbox_notify")] public bool ChatboxNotify { get; set; } = true;
    [JsonPropertyName("chatbox_template")] public string ChatboxTemplate { get; set; } = "{name}: {count}";

    public static CounterConfig Create(string name, string address = "/avatar/parameters/Example") => new() { Name = name, Address = address };
    public void Normalize(string name)
    {
        Name = name; Address ??= "/avatar/parameters/SomeParam"; ChatboxTemplate ??= "{name}: {count}";
        TriggerMode = TriggerMode == "int_eq" ? "int_eq" : "threshold";
    }
}

public sealed class GraphConfig
{
    [JsonPropertyName("counters")] public List<string> Counters { get; set; } = [];
    [JsonPropertyName("preset")] public string Preset { get; set; } = "all";
    [JsonPropertyName("bucket")] public string Bucket { get; set; } = "minute";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "total";
    [JsonPropertyName("graphtype")] public string GraphType { get; set; } = "line";
    [JsonPropertyName("autoY")] public bool AutoY { get; set; } = true;
    [JsonPropertyName("ymin")] public double? YMin { get; set; }
    [JsonPropertyName("ymax")] public double? YMax { get; set; }
    [JsonPropertyName("ymargin")] public int YMargin { get; set; }
    [JsonPropertyName("custom_start_ms")] public long? CustomStartMs { get; set; }
    [JsonPropertyName("custom_end_ms")] public long? CustomEndMs { get; set; }
    [JsonPropertyName("auto_refresh")] public bool AutoRefresh { get; set; } = true;
    [JsonPropertyName("auto_refresh_ms")] public int AutoRefreshMs { get; set; } = 60000;
    [JsonPropertyName("auto_follow")] public bool AutoFollow { get; set; } = true;
    [JsonPropertyName("graph_height_px")] public int GraphHeightPx { get; set; } = 320;
    [JsonPropertyName("mini_height_px")] public int MiniHeightPx { get; set; } = 160;
    [JsonPropertyName("name")] public string? Name { get; set; } = "Home Graph 1";
    public static GraphConfig Create(string name) => new() { Name = name };
    public void Normalize(string name)
    {
        Name = string.IsNullOrWhiteSpace(Name) ? name : Name;
        Counters ??= [];
        Counters = Counters.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
        Preset = Preset is "all" or "10m" or "30m" or "1h" or "2h" or "5h" or "12h" or "1d" or "7d" or "30d" or "custom" ? Preset : "1h";
        Bucket = Bucket is "minute" or "hour" or "day" or "auto" ? Bucket : "auto";
        Mode = Mode == "delta" ? "delta" : "total";
        GraphType = GraphType is "line" or "area" or "step" or "bar" ? GraphType : "line";
        AutoRefreshMs = Math.Clamp(AutoRefreshMs, 1_000, 3_600_000);
        GraphHeightPx = Math.Clamp(GraphHeightPx, 120, 1_200);
        MiniHeightPx = Math.Clamp(MiniHeightPx, 100, 1_200);
        if (YMin.HasValue && YMax.HasValue && YMin > YMax) (YMin, YMax) = (YMax, YMin);
        if (CustomStartMs.HasValue && CustomEndMs.HasValue && CustomStartMs > CustomEndMs)
            (CustomStartMs, CustomEndMs) = (CustomEndMs, CustomStartMs);
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
