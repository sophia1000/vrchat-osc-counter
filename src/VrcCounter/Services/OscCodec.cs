using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BlobHandles;
using Buildetech.OscCore;
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
    private readonly object _sendGate = new();
    private CancellationTokenSource? _listenerCts;
    private Task? _listener;
    private Socket? _senderSocket;
    private OscWriter? _senderWriter;
    private IPEndPoint? _output;
    private OSCQueryService? _oscQuery;
    private int _listenerRunning;
    private long _sentPacketCount;
    private long _lastSendMs;
    private string _lastSendAddress = "";
    private string _lastSendError = "";
    public int? OscQueryTcpPort { get; private set; }
    public bool OscQueryRunning => _oscQuery is not null;
    public string SelectedTransport => _state.Read(c => c.OscTransport);
    public bool TransportRunning => Volatile.Read(ref _listenerRunning) == 1;
    public bool LegacyOscRunning => SelectedTransport == AppConfig.LegacyOscTransport && TransportRunning;

    public OscService(AppState state)
    {
        _state = state;
        BlobString.Encoding = Encoding.UTF8;
        RebuildOutput();
    }

    public void RebuildOutput()
    {
        lock (_sendGate)
        {
            try { RebuildOutputLocked(); }
            catch (Exception ex)
            {
                Volatile.Write(ref _lastSendError, ex.Message);
                Console.Error.WriteLine($"[OSC] OscCore client could not initialize: {ex.Message}");
            }
        }
    }

    public OscSendStatus GetSendStatus() => new(
        Interlocked.Read(ref _sentPacketCount),
        Interlocked.Read(ref _lastSendMs),
        Volatile.Read(ref _lastSendAddress),
        Volatile.Read(ref _lastSendError));

    public Task<bool> SendAsync(string address, params object[] values)
    {
        lock (_sendGate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (_senderSocket is null || _senderWriter is null || _output is null) RebuildOutputLocked();
                    SendWithOscCore(_senderSocket!, _senderWriter!, _output!, address, values);
                    Interlocked.Increment(ref _sentPacketCount);
                    Interlocked.Exchange(ref _lastSendMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Volatile.Write(ref _lastSendAddress, address);
                    Volatile.Write(ref _lastSendError, "");
                    return Task.FromResult(true);
                }
                catch (Exception ex) when (attempt == 0)
                {
                    Console.Error.WriteLine($"[OSC] OscCore send failed, rebuilding client: {ex.Message}");
                    try { RebuildOutputLocked(); }
                    catch (Exception rebuildError)
                    {
                        Volatile.Write(ref _lastSendError, rebuildError.Message);
                        return Task.FromResult(false);
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _lastSendError, ex.Message);
                    Console.Error.WriteLine($"[ERROR] OscCore send: {ex.Message}");
                    return Task.FromResult(false);
                }
            }
        }
        return Task.FromResult(false);
    }

    private void RebuildOutputLocked()
    {
        var cfg = _state.Snapshot();
        var output = new IPEndPoint(IPAddress.Parse(cfg.OscOutIp), cfg.OscOutPort);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var sourceAddress = IPAddress.IsLoopback(output.Address) ? IPAddress.Loopback : IPAddress.Any;
        socket.Bind(new IPEndPoint(sourceAddress, 0));
        var writer = new OscWriter();
        var previousSocket = _senderSocket;
        var previousWriter = _senderWriter;
        _senderSocket = socket;
        _senderWriter = writer;
        _output = output;
        previousSocket?.Dispose();
        previousWriter?.Dispose();
    }

    private static void SendWithOscCore(Socket socket, OscWriter writer, IPEndPoint output, string address, IReadOnlyList<object> values)
    {
        writer.Reset();
        writer.Write(address);

        var tags = new StringBuilder(",");
        foreach (var value in values)
            tags.Append(value switch
            {
                int => 'i',
                long => 'h',
                float => 'f',
                double => 'd',
                string => 's',
                bool b => b ? 'T' : 'F',
                _ => 's'
            });
        writer.Write(tags.ToString());

        foreach (var value in values)
        {
            switch (value)
            {
                case int i: writer.Write(i); break;
                case long l: writer.Write(l); break;
                case float f: writer.Write(f); break;
                case double d: writer.Write(d); break;
                case string s: writer.Write(new BlobString(s)); break;
                case bool: break;
                default: writer.Write(new BlobString(value?.ToString() ?? "")); break;
            }
        }

        socket.SendTo(writer.Buffer, 0, writer.Length, SocketFlags.None, output);
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
        lock (_sendGate)
        {
            _senderSocket?.Dispose();
            _senderWriter?.Dispose();
            _senderSocket = null;
            _senderWriter = null;
            _output = null;
        }
    }
}

public sealed record OscSendStatus(long SentPacketCount, long LastSendMs, string LastSendAddress, string LastSendError);
