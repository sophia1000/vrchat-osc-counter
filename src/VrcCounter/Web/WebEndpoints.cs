using System.Globalization;
using System.Text.Json;
using VrcCounter.Models;
using VrcCounter.Services;

namespace VrcCounter.Web;

public static class WebEndpoints
{
    public static void MapVrcCounter(this WebApplication app, AppState state, HtmlRenderer html)
    {
        app.MapGet("/", () => Results.Content(html.Index(), "text/html"));
        app.MapGet("/graph", () => Results.Content(html.Graph(), "text/html"));
        app.MapGet("/events", (HttpResponse response, CancellationToken token) => state.Sse.StreamAsync(response, token));
        app.MapGet("/api/state", () =>
        {
            var cfg = state.Snapshot();
            return Results.Json(new { rev = state.Revision, counters = cfg.CounterOrder.Where(cfg.Counters.ContainsKey).Select(n => { var c = cfg.Counters[n]; return new { name = n, count = c.Count, send_chatbox = c.SendChatbox, address = c.Address }; }) });
        });
        app.MapGet("/api/counters", () => Results.Json(state.Read(c => c.Counters.Keys.ToArray())));
        app.MapGet("/api/home-graphs-lite", () => { var c = state.Snapshot(); return Results.Json(new { cols = c.HomeGraphsColumns, order = c.GraphOrder, html = "" }); });
        app.MapPost("/api/counter-order", async (HttpRequest req) =>
        {
            var data = await JsonSerializer.DeserializeAsync<OrderRequest>(req.Body, JsonOptions.Default) ?? new(); state.SetCounterOrder(data.Order); return Results.Json(new { ok = true });
        });
        app.MapGet("/api/series_multi", async (HttpRequest req) =>
        {
            var query = req.Query; var names = query["counter"].SelectMany(x => (x ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var start = ParseLong(query["start_ms"].FirstOrDefault(), now - 3_600_000); var end = ParseLong(query["end_ms"].FirstOrDefault(), now);
            var bucket = query["bucket"].FirstOrDefault() ?? "hour"; var mode = query["mode"].FirstOrDefault() ?? "total"; var cfg = state.Snapshot();
            if (names.Count == 0) names = cfg.Counters.Keys.ToList(); else names = names.Where(cfg.Counters.ContainsKey).ToList();
            var available = await state.Events.GetRangeAsync(names);
            if (IsTrue(query["all"].FirstOrDefault()) && available is not null)
            {
                start = available.StartMs;
                end = Math.Max(available.EndMs, start + 1);
            }
            if (end < start) (start, end) = (end, start);
            var series = new List<EventSeries>(); foreach (var name in names) series.Add(await state.Events.GetSeriesAsync(name, start, end, bucket, mode, cfg.Counters[name].Count));
            return Results.Json(new
            {
                series,
                rangeStartMs = start,
                rangeEndMs = end,
                availableStartMs = available?.StartMs,
                availableEndMs = available?.EndMs,
                eventCount = available?.EventCount ?? 0
            });
        });
        app.MapMethods("/api/graph-prefs", ["GET", "POST"], async (HttpRequest req) =>
        {
            var id = req.Query["gid"].FirstOrDefault() ?? "g1";
            if (HttpMethods.IsGet(req.Method))
            {
                var graph = state.GetGraph(id); var names = state.Read(c => c.Counters.Keys.ToHashSet()); graph.Counters = graph.Counters.Where(names.Contains).ToList(); return Results.Json(graph);
            }
            var update = await JsonSerializer.DeserializeAsync<GraphConfig>(req.Body, JsonOptions.Default) ?? state.GetGraph(id); state.SetGraph(id, update); return Results.Json(new { ok = true });
        });
        app.MapPost("/api/graph-notify", async (HttpRequest req) =>
        {
            var data = await JsonSerializer.DeserializeAsync<GraphNotify>(req.Body, JsonOptions.Default) ?? new();
            if (!string.IsNullOrEmpty(data.Type)) state.Sse.Publish(new { type = data.Type, gid = data.Gid, name = data.Name }); return Results.Json(new { ok = true });
        });
        app.MapPost("/rename-graph", async (HttpRequest req) =>
        {
            var data = await JsonSerializer.DeserializeAsync<RenameGraph>(req.Body, JsonOptions.Default) ?? new();
            if (string.IsNullOrWhiteSpace(data.Gid) || string.IsNullOrWhiteSpace(data.Name)) return Results.BadRequest(new { ok = false });
            return state.RenameGraph(data.Gid, data.Name) ? Results.Json(new { ok = true }) : Results.NotFound();
        });
        app.MapPost("/toggle-chatbox", async (HttpRequest req) => { var f = await req.ReadFormAsync(); var name = f["name"].ToString(); state.ToggleChatbox(name, IsTrue(f["on"])); return Results.NoContent(); }).DisableAntiforgery();
        app.MapGet("/add", () => Results.Redirect("/edit/" + Uri.EscapeDataString(state.AddCounter())));
        app.MapMethods("/edit/{**encoded}", ["GET", "POST"], async (string encoded, HttpRequest req) =>
        {
            var name = Uri.UnescapeDataString(encoded);
            if (HttpMethods.IsGet(req.Method)) { var page = html.Edit(name); return page.Length == 0 ? Results.NotFound("Not found") : Results.Content(page, "text/html"); }
            var f = await req.ReadFormAsync(); var original = f["original_name"].ToString(); if (string.IsNullOrEmpty(original)) original = name; var newName = f["name"].ToString().Trim(); if (string.IsNullOrEmpty(newName)) newName = name;
            var ok = state.UpdateCounter(original, newName, c =>
            {
                c.Address = ValueOr(f["address"], c.Address); c.Count = ParseLong(f["count"], c.Count); c.TriggerMode = f["trigger_mode"] == "int_eq" ? "int_eq" : "threshold";
                c.Threshold = ParseDouble(f["threshold"], c.Threshold); c.ReleaseThreshold = ParseDouble(f["release_threshold"], c.ReleaseThreshold); c.IntValue = (int)ParseLong(f["int_value"], c.IntValue); c.DebounceMs = (int)ParseLong(f["debounce_ms"], c.DebounceMs);
                c.SendChatbox = IsTrue(f["send_chatbox"]); c.ChatboxNotify = IsTrue(f["chatbox_notify"]); c.ChatboxTemplate = ValueOr(f["chatbox_template"], c.ChatboxTemplate);
            }, out var duplicate);
            if (duplicate) return Results.BadRequest("Name already exists"); if (!ok) return Results.NotFound("Not found"); await state.Osc.RestartAsync(); return Results.Redirect("/");
        }).DisableAntiforgery();
        app.MapGet("/delete/{**encoded}", async (string encoded) => { state.DeleteCounter(Uri.UnescapeDataString(encoded)); await state.Osc.RestartAsync(); return Results.Redirect("/"); });
        app.MapGet("/add-graph", () => Results.Redirect("/graph?gid=" + Uri.EscapeDataString(state.AddGraph())));
        app.MapGet("/delete-graph/{id}", (string id) => { state.DeleteGraph(id); return Results.Redirect("/"); });
        app.MapPost("/api/home-graph-row-height", async (HttpRequest req) => { var d = await JsonSerializer.DeserializeAsync<RowHeight>(req.Body, JsonOptions.Default) ?? new(); state.SetRowHeight(d.Row, d.Height); return Results.Json(new { ok = true }); });
        app.MapPost("/update-global", async (HttpRequest req) =>
        {
            var f = await req.ReadFormAsync(); var changes = state.UpdateGlobal(c =>
            {
                c.OscInIp = ValueOr(f["osc_in_ip"], c.OscInIp); c.OscInPort = (int)ParseLong(f["osc_in_port"], c.OscInPort); c.OscOutIp = ValueOr(f["osc_out_ip"], c.OscOutIp); c.OscOutPort = (int)ParseLong(f["osc_out_port"], c.OscOutPort);
                c.WebUiBind = ValueOr(f["web_ui_bind"], c.WebUiBind); c.WebUiPort = (int)ParseLong(f["web_ui_port"], c.WebUiPort); c.SaveThrottleMs = (int)ParseLong(f["save_throttle_ms"], c.SaveThrottleMs);
                var mode = f["chatbox_mode"].ToString(); c.ChatboxMode = mode is "modern" or "legacy2" or "both" ? mode : "modern"; c.ChatboxPerMinuteLimit = (int)ParseLong(f["chatbox_per_minute_limit"], c.ChatboxPerMinuteLimit); c.ChatboxMinIntervalMs = (int)ParseLong(f["chatbox_min_interval_ms"], c.ChatboxMinIntervalMs); c.ChatboxAutoClearMs = (int)ParseLong(f["chatbox_auto_clear_ms"], c.ChatboxAutoClearMs);
                c.ChatboxEnabledByDefault = IsTrue(f["chatbox_enabled_by_default"]); c.ChatboxNotifyByDefault = IsTrue(f["chatbox_notify_by_default"]); c.CountersCompact = IsTrue(f["counters_compact"]); c.HomeGraphsColumns = Math.Clamp((int)ParseLong(f["home_graphs_columns"], c.HomeGraphsColumns), 1, 3);
            });
            if (changes.RebuildOutput) state.Osc.RebuildOutput(); if (changes.RestartInput) await state.Osc.RestartAsync(); return Results.Redirect("/");
        }).DisableAntiforgery();
        app.MapPost("/api/window-size", async (HttpRequest req) => { var d = await JsonSerializer.DeserializeAsync<WindowSize>(req.Body, JsonOptions.Default) ?? new(); if (d.W > 0 && d.H > 0) state.SetWindowSize(d.W, d.H); return Results.Json(new { ok = true }); });
        app.MapGet("/api/oscquery/status", () => Results.Json(new { running = state.Osc.OscQueryRunning, tcpPort = state.Osc.OscQueryTcpPort, oscPort = state.Read(c => c.OscInPort) }));
        app.MapGet("/health", () => Results.Json(new { ok = true, oscquery = new { running = state.Osc.OscQueryRunning, tcpPort = state.Osc.OscQueryTcpPort } }));
    }

    private static bool IsTrue(string? value) => value is "1" or "true" or "True" or "on";
    private static string ValueOr(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;
    private static long ParseLong(string? value, long fallback) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    private static double ParseDouble(string? value, double fallback) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    private sealed class OrderRequest { public List<string> Order { get; set; } = []; }
    private sealed class GraphNotify { public string Type { get; set; } = ""; public string Gid { get; set; } = ""; public string? Name { get; set; } }
    private sealed class RenameGraph { public string Gid { get; set; } = ""; public string Name { get; set; } = ""; }
    private sealed class RowHeight { public int Row { get; set; } public int Height { get; set; } = 220; }
    private sealed class WindowSize { public int W { get; set; } public int H { get; set; } }
}
