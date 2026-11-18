namespace EventRelay.Infrastructure.Persistence;

/// <summary>
/// A broker message a consumer has already handled. Inserted in the same transaction as the
/// consumer's side effects, so a redelivered message is recognised and acknowledged without
/// repeating them (idempotent consumer).
/// </summary>
public sealed class ProcessedMessage
{
    public required string Consumer { get; init; }

    public required string MessageId { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}
