using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace VrcCounter.Services;

public sealed class SseBroker
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
    public void Publish(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        foreach (var channel in _subscribers.Values) channel.Writer.TryWrite(json);
    }

    public async Task StreamAsync(HttpResponse response, CancellationToken token)
    {
        response.ContentType = "text/event-stream"; response.Headers.CacheControl = "no-cache";
        var id = Guid.NewGuid(); var channel = Channel.CreateUnbounded<string>(); _subscribers[id] = channel;
        try
        {
            await response.WriteAsync("event: hello\ndata: {\"ok\":true}\n\n", token); await response.Body.FlushAsync(token);
            while (!token.IsCancellationRequested)
            {
                var wait = channel.Reader.WaitToReadAsync(token).AsTask();
                var ping = Task.Delay(TimeSpan.FromSeconds(5), token);
                if (await Task.WhenAny(wait, ping) == wait && await wait)
                    while (channel.Reader.TryRead(out var item)) await response.WriteAsync($"data: {item}\n\n", token);
                else await response.WriteAsync("event: ping\ndata: {}\n\n", token);
                await response.Body.FlushAsync(token);
            }
        }
        catch (OperationCanceledException) { }
        finally { _subscribers.TryRemove(id, out _); }
    }
}
