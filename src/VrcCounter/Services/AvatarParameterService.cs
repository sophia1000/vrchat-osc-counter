using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using VRC.OSCQuery;

namespace VrcCounter.Services;

public sealed record AvatarParameter(string Name, string Address, string Type, string Value, bool CanCount);
public sealed record AvatarParameterList(string Status, string Message, string? AvatarId, IReadOnlyList<AvatarParameter> Parameters);

/// <summary>Reads VRChat's current OSCQuery tree, never an arbitrary saved avatar.</summary>
public sealed class AvatarParameterService(OscService osc) : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AvatarParameterList? _cached;
    private DateTimeOffset _readAt;
    private string? _avatarAtRead;

    public async Task<AvatarParameterList> GetAsync(bool refresh, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!osc.OscQueryRunning)
                return new("offline", "Use OSCQuery in Global settings to browse the live avatar. You can still enter an OSC address manually.", null, []);
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
            _cached = chosen ?? (live.Length > 1
                ? new("ambiguous", "Multiple VRChat clients were found. Switch avatar in the client you want to use, then refresh.", null, [])
                : new("offline", "VRChat was not found. Start VRChat, enable OSC, and load an avatar. This list refreshes automatically.", null, []));
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

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
