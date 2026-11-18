namespace EventRelay.Infrastructure.Outbox;

/// <summary>
/// A message waiting to be published to the broker. Written in the same transaction as the
/// event it describes (transactional outbox, see docs/adr/001-transactional-outbox.md).
/// </summary>
public sealed class OutboxMessage
{
    public const int MaxErrorLength = 500;

    private OutboxMessage()
    {
    }

    /// <summary>Monotonic identity: gives the publisher a stable FIFO order.</summary>
    public long Id { get; private set; }

    /// <summary>Becomes the Service Bus MessageId, which consumers use for de-duplication.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>Event type, published as the Service Bus Subject.</summary>
    public string Type { get; private set; } = null!;

    /// <summary>Serialized <see cref="Messaging.EventEnvelope"/>.</summary>
    public string Payload { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessage Create(Guid messageId, string type, string payload, DateTimeOffset now) => new()
    {
        MessageId = messageId,
        Type = type,
        Payload = payload,
        CreatedAt = now.ToUniversalTime(),
    };

    public void MarkPublished(DateTimeOffset now)
    {
        Attempts++;
        ProcessedAt = now.ToUniversalTime();
        LastError = null;
    }

    public void RecordFailure(string error)
    {
        Attempts++;
        LastError = error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
    }
}
