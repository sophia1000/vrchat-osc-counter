using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VrcCounter.Models;
using VRC.OSCQuery;

namespace VrcCounter.Services;

public static class OscCodec
{
    public static byte[] Encode(string address, params object[] values)
    {
        using var stream = new MemoryStream(); WriteString(stream, address);
        var tags = new StringBuilder(",");
        foreach (var value in values) tags.Append(value switch { int => 'i', long => 'h', float => 'f', double => 'd', string => 's', bool b => b ? 'T' : 'F', _ => 's' });
        WriteString(stream, tags.ToString());
        Span<byte> bytes = stackalloc byte[8];
        foreach (var value in values)
        {
            switch (value)
            {
                case int i: BinaryPrimitives.WriteInt32BigEndian(bytes, i); stream.Write(bytes[..4]); break;
                case long l: BinaryPrimitives.WriteInt64BigEndian(bytes, l); stream.Write(bytes); break;
                case float f: BinaryPrimitives.WriteInt32BigEndian(bytes, BitConverter.SingleToInt32Bits(f)); stream.Write(bytes[..4]); break;
                case double d: BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(d)); stream.Write(bytes); break;
                case string s: WriteString(stream, s); break;
                case bool: break;
                default: WriteString(stream, value?.ToString() ?? ""); break;
            }
        }
        return stream.ToArray();
    }

    public static IReadOnlyList<(string Address, object[] Values)> Decode(ReadOnlyMemory<byte> packet)
    {
        var messages = new List<(string Address, object[] Values)>();
        DecodeInto(packet, messages); return messages;
    }

    private static void DecodeInto(ReadOnlyMemory<byte> packet, List<(string Address, object[] Values)> messages)
    {
        if (packet.Length == 0) return;
        ReadOnlySpan<byte> data = packet.Span; var offset = 0; var first = ReadString(data, ref offset);
        if (first == "#bundle")
        {
            if (offset + 8 > data.Length) return; offset += 8;
            while (offset + 4 <= data.Length)
            {
                var size = BinaryPrimitives.ReadInt32BigEndian(data[offset..]); offset += 4;
                if (size <= 0 || offset + size > data.Length) return;
                DecodeInto(packet.Slice(offset, size), messages);
                offset += size;
            }
            return;
        }
        if (string.IsNullOrEmpty(first) || offset >= data.Length) return;
        var tags = ReadString(data, ref offset); if (!tags.StartsWith(',')) return;
        var values = new List<object>();
        foreach (var tag in tags.AsSpan(1))
        {
            if (offset > data.Length) return;
            switch (tag)
            {
                case 'i': if (offset + 4 > data.Length) return; values.Add(BinaryPrimitives.ReadInt32BigEndian(data[offset..])); offset += 4; break;
                case 'h': if (offset + 8 > data.Length) return; values.Add(BinaryPrimitives.ReadInt64BigEndian(data[offset..])); offset += 8; break;
                case 'f': if (offset + 4 > data.Length) return; values.Add(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data[offset..]))); offset += 4; break;
                case 'd': if (offset + 8 > data.Length) return; values.Add(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data[offset..]))); offset += 8; break;
                case 's': values.Add(ReadString(data, ref offset)); break;
                case 'T': values.Add(true); break; case 'F': values.Add(false); break;
                case 'b':
                    if (offset + 4 > data.Length) return; var len = BinaryPrimitives.ReadInt32BigEndian(data[offset..]); offset += 4;
                    if (len < 0 || offset + len > data.Length) return; values.Add(data.Slice(offset, len).ToArray()); offset = Align4(offset + len); break;
                default: return;
            }
        }
        messages.Add((first, values.ToArray()));
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); stream.Write(bytes); stream.WriteByte(0);
        while (stream.Position % 4 != 0) stream.WriteByte(0);
    }
    private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        var start = offset; while (offset < data.Length && data[offset] != 0) offset++;
        if (offset >= data.Length) { offset = data.Length; return ""; }
        var result = Encoding.UTF8.GetString(data[start..offset]); offset = Align4(offset + 1); return result;
    }
    private static int Align4(int value) => (value + 3) & ~3;
}

public sealed class OscService : IAsyncDisposable
{
    private readonly AppState _state;
    private CancellationTokenSource? _listenerCts;
    private Task? _listener;
    private UdpClient? _sender;
    private IPEndPoint? _output;
    private OSCQueryService? _oscQuery;
    private int _listenerRunning;
    public int? OscQueryTcpPort { get; private set; }
    public bool OscQueryRunning => _oscQuery is not null;
    public string SelectedTransport => _state.Read(c => c.OscTransport);
    public bool TransportRunning => Volatile.Read(ref _listenerRunning) == 1;
    public bool LegacyOscRunning => SelectedTransport == AppConfig.LegacyOscTransport && TransportRunning;

    public OscService(AppState state) { _state = state; RebuildOutput(); }

    public void RebuildOutput()
    {
        var cfg = _state.Snapshot();
        _sender?.Dispose(); _sender = new UdpClient(AddressFamily.InterNetwork);
        _output = new IPEndPoint(IPAddress.Parse(cfg.OscOutIp), cfg.OscOutPort);
    }

    public async Task SendAsync(string address, params object[] values)
    {
        try
        {
            var sender = _sender; var endpoint = _output; if (sender is null || endpoint is null) return;
            var packet = OscCodec.Encode(address, values); await sender.SendAsync(packet, endpoint);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[ERROR] OSC send: {ex.Message}"); }
    }

    public async Task RestartAsync()
    {
        StopOscQuery();
        if (_listenerCts is not null) { await _listenerCts.CancelAsync(); if (_listener is not null) try { await _listener; } catch { } _listenerCts.Dispose(); }
        _listenerCts = new CancellationTokenSource(); _listener = ListenLoopAsync(_listenerCts.Token);
        if (SelectedTransport == AppConfig.OscQueryTransport) StartOscQuery();
    }

    private void StartOscQuery()
    {
        try
        {
            var cfg = _state.Snapshot();
            var queryPort = VRC.OSCQuery.Extensions.GetAvailableTcpPort();
            var oscIp = IPAddress.TryParse(cfg.OscInIp, out var configured) && !configured.Equals(IPAddress.Any)
                ? configured
                : IPAddress.Loopback;
            var serviceName = $"VRC Counter-{Environment.MachineName}";
            _oscQuery = new OSCQueryServiceBuilder()
                .WithServiceName(serviceName)
                .WithHostIP(IPAddress.Loopback)
                .WithOscIP(oscIp)
                .WithTcpPort(queryPort)
                .WithUdpPort(cfg.OscInPort)
                .WithDefaults()
                .Build();

            foreach (var counter in cfg.Counters.Values
                         .Where(c => !string.IsNullOrWhiteSpace(c.Address))
                         .GroupBy(c => c.Address, StringComparer.Ordinal)
                         .Select(g => g.First()))
            {
                var oscType = counter.TriggerMode == "int_eq" ? "i" : "f";
                _oscQuery.AddEndpoint(counter.Address, oscType, Attributes.AccessValues.WriteOnly,
                    description: $"VRChat Counter input for {counter.Name}");
            }

            _oscQuery.RefreshServices();
            OscQueryTcpPort = queryPort;
            Console.WriteLine($"[OSCQuery] Advertising {serviceName} at HTTP {queryPort}, OSC {oscIp}:{cfg.OscInPort}");
        }
        catch (Exception ex)
        {
            StopOscQuery();
            Console.Error.WriteLine($"[OSCQuery] Could not start: {ex.Message}");
        }
    }

    private void StopOscQuery()
    {
        try { _oscQuery?.Dispose(); }
        catch (Exception ex) { Console.Error.WriteLine($"[OSCQuery] Stop error: {ex.Message}"); }
        _oscQuery = null;
        OscQueryTcpPort = null;
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpClient? udp = null;
            try
            {
                var cfg = _state.Snapshot(); udp = new UdpClient(new IPEndPoint(IPAddress.Parse(cfg.OscInIp), cfg.OscInPort));
                Volatile.Write(ref _listenerRunning, 1);
                var label = cfg.OscTransport == AppConfig.OscQueryTransport ? "OSCQuery" : "Legacy OSC";
                Console.WriteLine($"[{label}] Listening on {cfg.OscInIp}:{cfg.OscInPort}");
                while (!token.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(token);
                    foreach (var message in OscCodec.Decode(result.Buffer)) _ = _state.HandleOscAsync(message.Address, message.Values);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[OSC] Listener error: {ex.Message}; retrying in 3s");
                try { await Task.Delay(3000, token); } catch (OperationCanceledException) { break; }
            }
            finally { Volatile.Write(ref _listenerRunning, 0); udp?.Dispose(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopOscQuery();
        if (_listenerCts is not null) { await _listenerCts.CancelAsync(); if (_listener is not null) try { await _listener; } catch { } _listenerCts.Dispose(); }
        _sender?.Dispose();
    }
}
