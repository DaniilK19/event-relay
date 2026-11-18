using System.Text.Json;
using EventRelay.Core;

namespace EventRelay.Api.Endpoints;

// Request properties are nullable on purpose: missing fields are reported as validation
// errors (400 with field names) instead of opaque deserialization failures.

public sealed record PublishEventRequest(
    Guid? Id,
    string? Type,
    string? Source,
    DateTimeOffset? Time,
    Dictionary<string, string>? Attributes,
    JsonElement? Data);

public sealed record EventAcceptedResponse(Guid Id, bool Duplicate);

public sealed record EventResponse(
    Guid Id,
    string Type,
    string Source,
    DateTimeOffset Time,
    IReadOnlyDictionary<string, string> Attributes,
    JsonElement? Data,
    DateTimeOffset ReceivedAt);

public sealed record DeliveryResponse(
    Guid Id,
    Guid EventId,
    Guid SubscriptionId,
    DeliveryStatus Status,
    int Attempts,
    int? LastStatusCode,
    string? LastError,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? CompletedAt)
{
    public static DeliveryResponse From(Delivery d) => new(
        d.Id, d.EventId, d.SubscriptionId, d.Status, d.Attempts, d.LastStatusCode,
        d.LastError, d.NextAttemptAt, d.LastAttemptAt, d.CompletedAt);
}

/// <param name="Secret">Optional. When omitted a random secret is generated and returned once.</param>
public sealed record CreateSubscriptionRequest(
    string? Name,
    string[]? EventTypes,
    Dictionary<string, string>? Filter,
    string? WebhookUrl,
    string? Secret);

/// <param name="Secret">Optional. When present, replaces (rotates) the signing secret.</param>
public sealed record UpdateSubscriptionRequest(
    string? Name,
    string[]? EventTypes,
    Dictionary<string, string>? Filter,
    string? WebhookUrl,
    bool? IsActive,
    string? Secret);

/// <summary>Subscription as returned by reads. The secret is never included.</summary>
public sealed record SubscriptionResponse(
    Guid Id,
    string Name,
    string[] EventTypes,
    IReadOnlyDictionary<string, string> Filter,
    string WebhookUrl,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static SubscriptionResponse From(Subscription s) => new(
        s.Id, s.Name, s.EventTypes, s.Filter, s.WebhookUrl, s.IsActive, s.CreatedAt, s.UpdatedAt);
}

/// <summary>Returned once, on creation: the only time the generated secret is visible.</summary>
public sealed record SubscriptionCreatedResponse(SubscriptionResponse Subscription, string Secret);
