namespace EventRelay.Core;

/// <summary>
/// An event accepted by EventRelay. The shape is loosely based on the CloudEvents envelope:
/// routing information lives in <see cref="Type"/> and <see cref="Attributes"/>,
/// the payload in <see cref="Data"/> is opaque to the relay.
/// </summary>
public sealed class Event
{
    // Used by EF Core when materializing.
    private Event()
    {
    }

    public Event(
        Guid id,
        string type,
        string source,
        DateTimeOffset time,
        IReadOnlyDictionary<string, string>? attributes,
        string? data,
        DateTimeOffset receivedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Event id must not be empty.", nameof(id));
        }

        if (!EventTypeRules.IsValidType(type))
        {
            throw new ArgumentException($"'{type}' is not a valid event type.", nameof(type));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (AttributeRules.Validate(attributes) is { } problem)
        {
            throw new ArgumentException(problem, nameof(attributes));
        }

        Id = id;
        Type = type;
        Source = source;
        Time = time.ToUniversalTime();
        Attributes = attributes is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(attributes, StringComparer.Ordinal);
        Data = data;
        ReceivedAt = receivedAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }

    /// <summary>Event type, for example <c>flight.delayed</c>.</summary>
    public string Type { get; private set; } = null!;

    /// <summary>Producer identifier, for example <c>flight-status-simulator</c>.</summary>
    public string Source { get; private set; } = null!;

    /// <summary>When the event happened, according to the producer.</summary>
    public DateTimeOffset Time { get; private set; }

    /// <summary>Flat string attributes used by subscription filters.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Payload as JSON text. EventRelay never interprets it.</summary>
    public string? Data { get; private set; }

    /// <summary>When EventRelay accepted the event.</summary>
    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>
    /// True when <paramref name="other"/> describes the same logical event, which makes a
    /// repeated POST with the same id a harmless retry rather than a conflict.
    /// </summary>
    public bool IsSameEventAs(Event other) =>
        Id == other.Id
        && string.Equals(Type, other.Type, StringComparison.Ordinal)
        && string.Equals(Source, other.Source, StringComparison.Ordinal);
}
