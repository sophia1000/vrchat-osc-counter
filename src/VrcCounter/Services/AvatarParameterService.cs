using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using VRC.OSCQuery;

namespace VrcCounter.Services;

public sealed record AvatarParameter(string Name, string Address, string Type, string Value, bool CanCount);
public sealed record AvatarParameterList(string Status, string Message, string? AvatarId, IReadOnlyList<AvatarParameter> Parameters);

/// <summary>Reads the live avatar and retains its parameter definitions for offline setup.</summary>
public sealed class AvatarParameterService(OscService osc, string? cachePath = null) : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AvatarParameterList? _cached;
    private DateTimeOffset _readAt;
    private string? _avatarAtRead;
    private SavedAvatar? _lastKnown = LoadSaved(cachePath);
    private readonly CancellationTokenSource _stop = new();
    private Task? _background;

    public void Start() => _background ??= Task.Run(async () =>
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await GetAsync(false, _stop.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"[Parameters] {ex.Message}"); }
                await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    });

    private sealed record SavedAvatar(string? AvatarId, AvatarParameter[] Parameters, DateTimeOffset SeenAt);
    private static SavedAvatar? LoadSaved(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return null;
            var saved = JsonSerializer.Deserialize<SavedAvatar>(File.ReadAllText(path));
            return saved?.Parameters is not null && saved.Parameters.All(p => p is not null && p.Address?.StartsWith("/avatar/parameters/", StringComparison.Ordinal) == true && p.Name is not null && p.Type is not null)
                ? saved : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private AvatarParameterList Offline(string message) => _lastKnown is { } saved
        ? new("cached", $"Offline: showing your last detected avatar (saved {saved.SeenAt.LocalDateTime:g}). Parameter values are unavailable until VRChat reconnects.", saved.AvatarId, saved.Parameters)
        : new("offline", message + " No avatar has been saved yet. Open VRChat with OSCQuery enabled once to capture its parameters.", null, []);

    private async Task RememberAsync(AvatarParameterList live, CancellationToken token)
    {
        if (live.AvatarId is null && live.Parameters.Count == 0) return;
        // Values are live observations, not reliable offline defaults.
        var definitions = live.Parameters.Select(p => p with { Value = "" }).ToArray();
        if (_lastKnown is { } old && old.AvatarId == live.AvatarId && old.Parameters.SequenceEqual(definitions)) return;
        _lastKnown = new(live.AvatarId, definitions, DateTimeOffset.UtcNow);
        if (cachePath is null) return;
        try
        {
            await File.WriteAllTextAsync(cachePath + ".tmp", JsonSerializer.Serialize(_lastKnown), token);
            File.Move(cachePath + ".tmp", cachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"[Parameters] Could not save offline avatar: {ex.Message}"); }
    }

    public async Task<AvatarParameterList> GetAsync(bool refresh, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!osc.OscQueryRunning)
                return Offline("Use OSCQuery in Global settings to browse the live avatar.");
            var avatar = osc.CurrentAvatarId;
            if (!refresh && _cached is not null && avatar == _avatarAtRead && DateTimeOffset.UtcNow - _readAt < TimeSpan.FromSeconds(3))
                return _cached;

            var profiles = osc.GetQueryServices(refresh);
            if (profiles.Length == 0)
            {
                osc.GetQueryServices(true);
                // mDNS replies arrive asynchronously. The editor also retries while open.
                await Task.Delay(500, token);
                profiles = osc.GetQueryServices(false);
            }
            var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToHashSet();
            var candidates = profiles.Where(p => p is not null && p.name is not null && p.address is not null && p.port is > 0 and <= 65535
                && p.name.StartsWith("VRChat", StringComparison.OrdinalIgnoreCase)
                && (IPAddress.IsLoopback(p.address) || localAddresses.Contains(p.address)))
                .DistinctBy(p => (p.address, p.port)).Take(8).ToArray();
            var results = await Task.WhenAll(candidates.Select(async profile =>
            {
                try
                {
                    var uri = new UriBuilder("http", profile.address.ToString(), profile.port, "/").Uri;
                    using var response = await _http.GetAsync(uri, token);
                    response.EnsureSuccessStatusCode();
                    using var tree = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    return ParseTree(tree.RootElement);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
                { return null; }
            }));
            token.ThrowIfCancellationRequested();
            var live = results.Where(r => r is not null).Cast<AvatarParameterList>().ToArray();
            var chosen = live.FirstOrDefault(r => avatar is not null && r.AvatarId == avatar);
            if (chosen is null && live.Length == 1) chosen = live[0];
            if (chosen is not null) await RememberAsync(chosen, token);
            _cached = chosen ?? (live.Length > 1
                ? new("ambiguous", "Multiple VRChat clients were found. Switch avatar in the client you want to use, then refresh.", null, [])
                : Offline("VRChat was not found."));
            _readAt = DateTimeOffset.UtcNow;
            _avatarAtRead = avatar;
            return _cached;
        }
        finally { _gate.Release(); }
    }

    public static AvatarParameterList ParseTree(JsonElement root)
    {
        var parameters = new Dictionary<string, AvatarParameter>(StringComparer.Ordinal);
        string? avatarId = null;
        void Walk(JsonElement node, string fallbackPath, int depth)
        {
            if (node.ValueKind != JsonValueKind.Object || depth > 32) return;
            var path = node.TryGetProperty("FULL_PATH", out var full) && full.ValueKind == JsonValueKind.String
                ? full.GetString()! : fallbackPath;
            var type = node.TryGetProperty("TYPE", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
            var value = node.TryGetProperty("VALUE", out var v) ? v : default;
            var first = value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 ? value[0] : value;
            if (path == "/avatar/change" && first.ValueKind == JsonValueKind.String) avatarId = first.GetString();
            const string prefix = "/avatar/parameters/";
            if (path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length && type.Length > 0)
            {
                var readable = !node.TryGetProperty("ACCESS", out var access) || (access.TryGetInt32(out var a) && (a & 1) != 0);
                var numeric = type is "f" or "d" or "i" or "h" or "T" or "F" or "b";
                var label = type switch { "f" or "d" => "Float", "i" or "h" => "Int", "T" or "F" => "Bool", _ => type };
                var display = first.ValueKind == JsonValueKind.Undefined ? "" : first.ToString();
                parameters[path] = new(path[prefix.Length..], path, label, display.Length > 100 ? display[..100] : display, readable && numeric && type != "b");
            }
            if (node.TryGetProperty("CONTENTS", out var contents) && contents.ValueKind == JsonValueKind.Object)
                foreach (var child in contents.EnumerateObject()) Walk(child.Value, path.TrimEnd('/') + "/" + child.Name, depth + 1);
        }
        Walk(root, "", 0);
        var sorted = parameters.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new("connected", sorted.Length == 0 ? "VRChat is connected, but has not exposed any avatar parameters yet." : $"{sorted.Length} OSC parameters on your current avatar. Select one to use its address.", avatarId, sorted);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _background?.GetAwaiter().GetResult();
        _http.Dispose(); _gate.Dispose(); _stop.Dispose();
    }
}
