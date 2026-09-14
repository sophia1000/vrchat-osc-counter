using System.Text;
using System.Text.Json;
using VrcCounter.Models;

namespace VrcCounter.Services;

public sealed class ConfigStore(string path)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _pending;
    private long _lastSaveMs;
    private int _closing;
    public string Path { get; } = path;

    public async Task<AppConfig> LoadAsync()
    {
        if (!File.Exists(Path))
        {
            var created = AppConfig.CreateDefault();
            await WriteNowAsync(created);
            return created;
        }
        await using var input = File.OpenRead(Path);
        var cfg = await JsonSerializer.DeserializeAsync<AppConfig>(input, JsonOptions.Default) ?? AppConfig.CreateDefault();
        cfg.Normalize();
        return cfg;
    }

    public void Schedule(Func<AppConfig> snapshot, int throttleMs)
    {
        if (Volatile.Read(ref _closing) != 0) return;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastSaveMs) >= Math.Max(0, throttleMs) && _pending is null)
        {
            _ = WriteSafeAsync(snapshot());
            return;
        }
        var replacement = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _pending, replacement);
        old?.Cancel(); old?.Dispose();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Math.Max(0, throttleMs), replacement.Token);
                await WriteSafeAsync(snapshot());
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(Interlocked.CompareExchange(ref _pending, null, replacement), replacement)) replacement.Dispose();
            }
        });
    }

    public Task FlushAsync(AppConfig config) => WriteSafeAsync(config);

    public async Task CloseAsync(AppConfig config)
    {
        Interlocked.Exchange(ref _closing, 1);
        var pending = Interlocked.Exchange(ref _pending, null);
        if (pending is not null) { await pending.CancelAsync(); pending.Dispose(); }
        // Unlike background saves, a final-save error must stop an update.
        await WriteNowAsync(config, final: true);
    }

    private async Task WriteSafeAsync(AppConfig cfg)
    {
        try { await WriteNowAsync(cfg); }
        catch (Exception ex) { Console.Error.WriteLine($"[ERROR] Saving config: {ex}"); }
    }

    private async Task WriteNowAsync(AppConfig cfg, bool final = false)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (!final && Volatile.Read(ref _closing) != 0) return;
            var temp = Path + ".tmp";
            var json = JsonSerializer.Serialize(cfg, JsonOptions.Default) + Environment.NewLine;
            await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false));
            File.Move(temp, Path, true);
            Interlocked.Exchange(ref _lastSaveMs, Environment.TickCount64);
        }
        finally { _writeLock.Release(); }
    }
}
