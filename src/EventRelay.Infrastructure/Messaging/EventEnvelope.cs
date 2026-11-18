using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventRelay.Core;

namespace EventRelay.Infrastructure.Messaging;

/// <summary>
/// Wire format of an event: the Service Bus message body and the webhook request body.
/// </summary>
public sealed record EventEnvelope(
    Guid Id,
    string Type,
    string Source,
    DateTimeOffset Time,
    IReadOnlyDictionary<string, string> Attributes,
    JsonElement? Data)
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static EventEnvelope FromEvent(Event evt) => new(
        evt.Id,
        evt.Type,
        evt.Source,
        evt.Time,
        evt.Attributes,
        ParseData(evt.Data));

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);

    /// <summary>Parses a message body, reporting why it is unusable instead of throwing.</summary>
    public static bool TryParse(
        BinaryData body,
        [NotNullWhen(true)] out EventEnvelope? envelope,
        [NotNullWhen(false)] out string? error)
    {
        envelope = null;
        try
        {
            envelope = body.ToObjectFromJson<EventEnvelope>(SerializerOptions);
        }
        catch (JsonException exception)
        {
            error = $"Body is not a valid event envelope: {exception.Message}";
            return false;
        }

        // Records do not enforce non-nullable reference properties during deserialization.
        if (envelope is null || envelope.Id == Guid.Empty || string.IsNullOrEmpty(envelope.Type))
        {
            envelope = null;
            error = "Body is missing the event id or type.";
            return false;
        }

        error = null;
        return true;
    }

    private static JsonElement? ParseData(string? data)
    {
        if (data is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(data);
        return document.RootElement.Clone();
    }
}
