using System.Globalization;
using System.Text.RegularExpressions;
using VrcCounter.Models;

namespace VrcCounter.Services;

public sealed partial class AppState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CounterRuntime> _runtime;
    private long _revision;
    private AppConfig _config;
    public ConfigStore ConfigStore { get; }
    public EventRepository Events { get; }
    public SseBroker Sse { get; } = new();
    public OscService Osc { get; }
    public ChatboxService Chatbox { get; private set; }

    public AppState(AppConfig config, ConfigStore store, EventRepository events)
    {
        _config = config; ConfigStore = store; Events = events;
        _runtime = config.Counters.Keys.ToDictionary(x => x, _ => new CounterRuntime());
        Osc = new OscService(this); Chatbox = new ChatboxService(this);
    }

    public long Revision => Interlocked.Read(ref _revision);
    public AppConfig Snapshot() { lock (_gate) return _config.Clone(); }
    public T Read<T>(Func<AppConfig, T> reader) { lock (_gate) return reader(_config); }
    public void SaveSoon() { var delay = Read(c => c.SaveThrottleMs); ConfigStore.Schedule(Snapshot, delay); }
    public void Changed(object evt) { Interlocked.Increment(ref _revision); Sse.Publish(evt); SaveSoon(); }

    public async Task HandleOscAsync(string address, object[] values)
    {
        if (values.Length == 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var value = CoerceNumeric(values[0]);
        List<(string Name, long Count, bool Chatbox)> increments = [];
        lock (_gate)
        {
            foreach (var (name, counter) in _config.Counters.Where(x => x.Value.Address == address))
            {
                if (!_runtime.TryGetValue(name, out var runtime)) _runtime[name] = runtime = new CounterRuntime();
                var debouncePassed = now - runtime.LastTriggerMs >= counter.DebounceMs; var trigger = false;
                if (counter.TriggerMode == "int_eq") trigger = debouncePassed && (int)Math.Round(value, MidpointRounding.ToEven) == counter.IntValue;
                else if (!runtime.High && value > counter.Threshold && debouncePassed) { trigger = true; runtime.High = true; }
                else if (runtime.High && value < counter.ReleaseThreshold) runtime.High = false;
                if (!trigger) continue;
                counter.Count++; runtime.LastTriggerMs = now; increments.Add((name, counter.Count, counter.SendChatbox));
            }
        }
        foreach (var item in increments)
        {
            await Events.AddAsync(item.Name, item.Count, now);
            Interlocked.Increment(ref _revision); Sse.Publish(new { type = "count", name = item.Name, count = item.Count, ts = now });
            if (item.Chatbox) Chatbox.Changed(item.Name, now);
        }
        if (increments.Count > 0) SaveSoon();
    }

    public string AddCounter()
    {
        string name; lock (_gate)
        {
            var i = 1; do name = $"Counter {i++}"; while (_config.Counters.ContainsKey(name));
            var c = CounterConfig.Create(name); c.SendChatbox = _config.ChatboxEnabledByDefault; c.ChatboxNotify = _config.ChatboxNotifyByDefault;
            _config.Counters[name] = c; _config.CounterOrder.Add(name); _runtime[name] = new CounterRuntime();
        }
        Changed(new { type = "add", name }); return name;
    }

    public bool UpdateCounter(string originalName, string newName, Action<CounterConfig> update, out bool duplicate)
    {
        duplicate = false; object evt;
        lock (_gate)
        {
            if (!_config.Counters.TryGetValue(originalName, out var current)) return false;
            if (newName != originalName && _config.Counters.ContainsKey(newName)) { duplicate = true; return false; }
            update(current); current.Name = newName;
            if (newName != originalName)
            {
                _config.Counters.Remove(originalName); _config.Counters[newName] = current;
                _runtime[newName] = _runtime.Remove(originalName, out var rt) ? rt : new CounterRuntime();
                _config.CounterOrder = _config.CounterOrder.Select(x => x == originalName ? newName : x).ToList();
                evt = new { type = "rename", old = originalName, @new = newName };
            }
            else evt = new { type = "update", name = newName };
        }
        Changed(evt); return true;
    }

    public void DeleteCounter(string name)
    {
        var removed = false; lock (_gate) { removed = _config.Counters.Remove(name); _runtime.Remove(name); _config.CounterOrder.RemoveAll(x => x == name); }
        if (removed) Changed(new { type = "delete", name });
    }

    public void ToggleChatbox(string name, bool on)
    {
        var changed = false;
        lock (_gate) if (_config.Counters.TryGetValue(name, out var c)) { c.SendChatbox = on; changed = true; }
        if (changed) Changed(new { type = "counter_toggle", name, send_chatbox = on });
    }

    public bool SetCounterOrder(IEnumerable<string> requested)
    {
        lock (_gate)
        {
            var order = requested.Where(_config.Counters.ContainsKey).Distinct().ToList();
            order.AddRange(_config.Counters.Keys.Where(x => !order.Contains(x))); _config.CounterOrder = order;
        }
        Changed(new { type = "counter_order" }); return true;
    }

    public GraphConfig GetGraph(string id)
    {
        lock (_gate)
        {
            var graph = _config.Graphs.TryGetValue(id, out var found) ? found : GraphConfig.Create($"Home Graph {id}");
            return System.Text.Json.JsonSerializer.Deserialize<GraphConfig>(System.Text.Json.JsonSerializer.Serialize(graph, JsonOptions.Default), JsonOptions.Default)!;
        }
    }

    public void SetGraph(string id, GraphConfig graph)
    {
        lock (_gate) { graph.Counters = graph.Counters.Where(_config.Counters.ContainsKey).ToList(); _config.Graphs[id] = graph; if (!_config.GraphOrder.Contains(id)) _config.GraphOrder.Add(id); }
        Changed(new { type = "graph_prefs", gid = id });
    }

    public string AddGraph()
    {
        string id; int i = 1; lock (_gate) { do id = $"g{i++}"; while (_config.Graphs.ContainsKey(id)); _config.Graphs[id] = GraphConfig.Create($"Home Graph {i - 1}"); _config.GraphOrder.Add(id); }
        Changed(new { type = "graph_add" }); return id;
    }

    public void DeleteGraph(string id)
    {
        lock (_gate) { _config.Graphs.Remove(id); _config.GraphOrder.RemoveAll(x => x == id); }
        Changed(new { type = "graph_delete" });
    }

    public bool RenameGraph(string id, string name)
    {
        lock (_gate) { if (!_config.Graphs.TryGetValue(id, out var graph)) return false; graph.Name = name; }
        Changed(new { type = "graph_rename", gid = id, name }); return true;
    }

    public void SetRowHeight(int row, int height)
    {
        if (row < 0) return;
        height = Math.Clamp(height, 130, 1200);
        lock (_gate) { while (_config.HomeGraphRowHeights.Count <= row) _config.HomeGraphRowHeights.Add(0); _config.HomeGraphRowHeights[row] = height; }
        Changed(new { type = "graph_row_height", row, height });
    }

    public void SetWindowSize(int width, int height)
    {
        lock (_gate) if (_config.RememberWindowSize) { _config.LastWindowWidth = width; _config.LastWindowHeight = height; }
        SaveSoon();
    }

    public (bool RestartInput, bool RebuildOutput) UpdateGlobal(Action<AppConfig> update)
    {
        bool input, output; lock (_gate)
        {
            var oldIn = (_config.OscTransport, _config.OscInIp, _config.OscInPort); var oldOut = (_config.OscOutIp, _config.OscOutPort);
            update(_config); _config.Normalize(); input = oldIn != (_config.OscTransport, _config.OscInIp, _config.OscInPort); output = oldOut != (_config.OscOutIp, _config.OscOutPort);
        }
        Changed(new { type = "global" }); return (input, output);
    }

    public static double CoerceNumeric(object? value) => value switch
    {
        bool b => b ? 1 : 0, byte b => b, short s => s, int i => i, long l => l,
        float f => f, double d => d, decimal m => (double)m,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n, _ => 0
    };

    [GeneratedRegex("\\{([^{}]+)\\}")]
    private static partial Regex TemplateFieldRegex();
    public static string FormatTemplate(string template, string name, long count)
    {
        try
        {
            var formatted = count.ToString("N0", CultureInfo.InvariantCulture);
            return TemplateFieldRegex().Replace(template, m => m.Groups[1].Value.Trim() switch { "name" => name, "count" => formatted, _ => m.Value });
        }
        catch { return $"{name}: {count:N0}"; }
    }
}

public sealed class ChatboxService
{
    public const int VrchatBurstLimit = 5;
    public const int VrchatRateWindowMs = 5000;

    private readonly AppState _state; private readonly object _gate = new(); private readonly Dictionary<string, long> _pending = [];
    private readonly SemaphoreSlim _sendQueue = new(1, 1);
    private readonly Queue<long> _sent = []; private long _lastSent; private bool _flushScheduled; private long _clearGeneration;
    public ChatboxService(AppState state) => _state = state;
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    public Task<bool> SendTestAsync() => SendModeAsync(
        _state.Snapshot().ChatboxMode,
        $"VRChat Counter test {DateTime.Now:HH:mm:ss}",
        true);

    public void Changed(string name, long timestamp) { lock (_gate) _pending[name] = timestamp; _ = FlushOrScheduleAsync(); }
    private long WaitMs()
    {
        var cfg = _state.Snapshot(); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            while (_sent.Count > 0 && _sent.Peek() < now - 60_000) _sent.Dequeue();
            var intervalMs = Math.Max(AppConfig.VrchatChatboxMinimumIntervalMs, cfg.ChatboxMinIntervalMs);
            var interval = Math.Max(0, intervalMs - (now - _lastSent));
            var recent = _sent.Where(timestamp => timestamp > now - VrchatRateWindowMs).ToArray();
            var vrchatWindow = recent.Length >= VrchatBurstLimit
                ? Math.Max(0, recent[^VrchatBurstLimit] + VrchatRateWindowMs - now)
                : 0;
            var perMinute = _sent.Count >= Math.Max(1, cfg.ChatboxPerMinuteLimit) ? Math.Max(0, _sent.Peek() + 60_000 - now) : 0;
            return Math.Max(interval, Math.Max(vrchatWindow, perMinute));
        }
    }
    private async Task FlushOrScheduleAsync()
    {
        var wait = WaitMs();
        lock (_gate) { if (_flushScheduled) return; _flushScheduled = true; }
        if (wait > 0) await Task.Delay((int)Math.Min(int.MaxValue, wait + 20));
        lock (_gate) _flushScheduled = false;
        if (WaitMs() > 0) { _ = FlushOrScheduleAsync(); return; }
        var cfg = _state.Snapshot(); List<KeyValuePair<string, long>> pending;
        lock (_gate) pending = _pending.OrderBy(x => x.Value).ToList();
        var lines = new List<string>(); var notify = false;
        foreach (var item in pending)
            if (cfg.Counters.TryGetValue(item.Key, out var counter)) { lines.Add(AppState.FormatTemplate(counter.ChatboxTemplate, item.Key, counter.Count)); notify |= counter.ChatboxNotify; }
        if (lines.Count == 0) return;
        lock (_gate) foreach (var item in pending) _pending.Remove(item.Key);
        await SendModeAsync(cfg.ChatboxMode, string.Join("\n", lines), notify);
        var generation = Interlocked.Increment(ref _clearGeneration);
        if (cfg.ChatboxAutoClearMs > 0) _ = Task.Run(async () => { await Task.Delay(cfg.ChatboxAutoClearMs); if (Interlocked.Read(ref _clearGeneration) == generation) await SendModeAsync(_state.Snapshot().ChatboxMode, "", false); });
    }
    private async Task<bool> SendModeAsync(string mode, string text, bool notify)
    {
        var sent = false;
        if (mode is "modern" or "both") sent |= await SendPacketRateLimitedAsync(text, true, notify);
        if (mode is "legacy2" or "both") sent |= await SendPacketRateLimitedAsync(text, true);
        return sent;
    }

    private async Task<bool> SendPacketRateLimitedAsync(params object[] values)
    {
        await _sendQueue.WaitAsync();
        try
        {
            while (true)
            {
                var wait = WaitMs();
                if (wait <= 0) break;
                await Task.Delay((int)Math.Min(int.MaxValue, wait + 10));
            }

            var sent = await _state.Osc.SendAsync("/chatbox/input", values);
            if (sent)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                lock (_gate)
                {
                    _sent.Enqueue(now);
                    _lastSent = now;
                }
            }
            return sent;
        }
        finally { _sendQueue.Release(); }
    }
}
