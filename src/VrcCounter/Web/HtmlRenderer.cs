using System.Globalization;
using System.Net;
using System.Text;
using VrcCounter.Models;
using VrcCounter.Services;

namespace VrcCounter.Web;

public sealed class HtmlRenderer(AppState state, string templateRoot)
{
    private string Load(string name) => File.ReadAllText(Path.Combine(templateRoot, name));
    private static string H(object? value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
    private static string Selected(bool condition) => condition ? "selected" : "";

    public string Index()
    {
        var cfg = state.Snapshot(); var rows = new StringBuilder(); var tiles = new StringBuilder();
        foreach (var name in cfg.CounterOrder)
        {
            if (!cfg.Counters.TryGetValue(name, out var c)) continue; var enc = Uri.EscapeDataString(name);
            var pressed = c.SendChatbox ? "true" : "false";
            var on = c.SendChatbox ? " on" : "";
            rows.Append($"<tr draggable='true' data-name='{H(name)}'><td class='drag'>⋮⋮</td><td><span class='pill'>{H(name)}</span></td><td><code>{H(c.Address)}</code></td><td class='count'>{c.Count:N0}</td><td><button type='button' class='chatToggle{on}' role='switch' aria-checked='{pressed}' title='Toggle chatbox output'><span></span>Chatbox</button></td><td><a class='btn sm' href='/edit/{enc}'>Edit</a></td></tr>");
            tiles.Append($"<div class='tile' draggable='true' data-name='{H(name)}'><div><b>{H(name)}</b><strong class='count'>{c.Count:N0}</strong></div><footer><button type='button' class='chatToggle{on}' role='switch' aria-checked='{pressed}' title='Toggle chatbox output'><span></span>Chatbox</button><a class='btn sm' href='/edit/{enc}'>Edit</a></footer></div>");
        }
        var graphs = new StringBuilder(); var graphIndex = 0;
        foreach (var gid in cfg.GraphOrder)
        {
            if (!cfg.Graphs.TryGetValue(gid, out var g)) continue;
            var row = graphIndex / cfg.HomeGraphsColumns;
            var savedRowHeight = row < cfg.HomeGraphRowHeights.Count ? cfg.HomeGraphRowHeights[row] : 0;
            var legacyHeight = g.MiniHeightPx >= 1200 ? 160 : g.MiniHeightPx;
            var height = Math.Clamp(savedRowHeight > 0 ? savedRowHeight : legacyHeight, 130, 1200);
            graphs.Append($"<article class='graph' data-gid='{H(gid)}' data-row='{row}'><header><b>{H(g.Name)}</b><span><a class='btn sm' href='/graph?gid={Uri.EscapeDataString(gid)}'>Edit</a><a class='btn sm danger' href='/delete-graph/{Uri.EscapeDataString(gid)}' onclick=\"return confirm('Delete this graph?')\">Delete</a></span></header><div class='chartwrap' style='height:{height}px'><canvas></canvas></div></article>");
            graphIndex++;
        }
        return Replace(Load("index.html"), new()
        {
            ["rows"] = rows.Length > 0 ? rows.ToString() : "<tr><td colspan='6'><em>No counters yet.</em></td></tr>", ["tiles"] = tiles.ToString(), ["graphs"] = graphs.ToString(),
            ["oscquery_transport"] = Selected(cfg.OscTransport == AppConfig.OscQueryTransport), ["legacy_osc_transport"] = Selected(cfg.OscTransport == AppConfig.LegacyOscTransport),
            ["osc_in_ip"] = H(cfg.OscInIp), ["osc_in_port"] = cfg.OscInPort.ToString(), ["osc_out_ip"] = H(cfg.OscOutIp), ["osc_out_port"] = cfg.OscOutPort.ToString(),
            ["web_ui_bind"] = H(cfg.WebUiBind), ["web_ui_port"] = cfg.WebUiPort.ToString(), ["save_throttle_ms"] = cfg.SaveThrottleMs.ToString(),
            ["modern"] = Selected(cfg.ChatboxMode == "modern"), ["legacy2"] = Selected(cfg.ChatboxMode == "legacy2"), ["both"] = Selected(cfg.ChatboxMode == "both"),
            ["per_minute"] = cfg.ChatboxPerMinuteLimit.ToString(), ["min_interval"] = cfg.ChatboxMinIntervalMs.ToString(), ["auto_clear"] = cfg.ChatboxAutoClearMs.ToString(),
            ["default_chat_on"] = Selected(cfg.ChatboxEnabledByDefault), ["default_chat_off"] = Selected(!cfg.ChatboxEnabledByDefault),
            ["default_notify_on"] = Selected(cfg.ChatboxNotifyByDefault), ["default_notify_off"] = Selected(!cfg.ChatboxNotifyByDefault),
            ["compact_on"] = Selected(cfg.CountersCompact), ["compact_off"] = Selected(!cfg.CountersCompact), ["table_display"] = cfg.CountersCompact ? "none" : "block", ["grid_display"] = cfg.CountersCompact ? "grid" : "none",
            ["col1"] = Selected(cfg.HomeGraphsColumns == 1), ["col2"] = Selected(cfg.HomeGraphsColumns == 2), ["col3"] = Selected(cfg.HomeGraphsColumns == 3), ["columns"] = cfg.HomeGraphsColumns.ToString()
        });
    }

    public string Edit(string name)
    {
        var c = state.Read(cfg => cfg.Counters.TryGetValue(name, out var found) ? found : null); if (c is null) return "";
        return Replace(Load("edit.html"), new() { ["name"] = H(name), ["encoded_name"] = Uri.EscapeDataString(name), ["address"] = H(c.Address), ["count"] = c.Count.ToString(), ["threshold"] = c.Threshold.ToString(CultureInfo.InvariantCulture), ["release"] = c.ReleaseThreshold.ToString(CultureInfo.InvariantCulture), ["int_value"] = c.IntValue.ToString(), ["debounce"] = c.DebounceMs.ToString(), ["template"] = H(c.ChatboxTemplate), ["threshold_mode"] = Selected(c.TriggerMode == "threshold"), ["int_mode"] = Selected(c.TriggerMode == "int_eq"), ["chat_on"] = Selected(c.SendChatbox), ["chat_off"] = Selected(!c.SendChatbox), ["notify_on"] = Selected(c.ChatboxNotify), ["notify_off"] = Selected(!c.ChatboxNotify) });
    }
    public string Graph() => Load("graph.html");
    private static string Replace(string input, Dictionary<string, string> values) { foreach (var (k, v) in values) input = input.Replace("{{" + k + "}}", v); return input; }
}
