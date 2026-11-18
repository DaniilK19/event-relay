using System.Collections.Concurrent;
using System.Text.Json;

namespace WebhookReceiver;

public sealed record ReceivedEvent(string Endpoint, Guid EventId, DateTimeOffset ReceivedAt, JsonElement Payload);

/// <summary>
/// Remembers recent deliveries. EventRelay guarantees at-least-once delivery, so a well-behaved
/// subscriber de-duplicates by the event id from the signed body (not by the unsigned X-Event-Id
/// header); this is the in-memory version of that.
/// </summary>
public sealed class ReceivedEvents
{
    private const int Capacity = 1_000;

    private readonly ConcurrentDictionary<(string Endpoint, Guid EventId), byte> seen = new();
    private readonly ConcurrentQueue<ReceivedEvent> latest = new();

    /// <returns>False when this endpoint already received the event.</returns>
    public bool TryAdd(ReceivedEvent received)
    {
        if (!seen.TryAdd((received.Endpoint, received.EventId), 0))
        {
            return false;
        }

        latest.Enqueue(received);
        while (latest.Count > Capacity && latest.TryDequeue(out var oldest))
        {
            seen.TryRemove((oldest.Endpoint, oldest.EventId), out _);
        }

        return true;
    }

    public IReadOnlyList<ReceivedEvent> Latest(int count) => latest.Reverse().Take(count).ToArray();
}
