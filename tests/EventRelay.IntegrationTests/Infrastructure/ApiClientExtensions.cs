using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventRelay.Core;

namespace EventRelay.IntegrationTests.Infrastructure;

public sealed record CreatedSubscription(Guid Id, string Secret);

public sealed record DeliveryView(
    Guid Id,
    Guid SubscriptionId,
    DeliveryStatus Status,
    int Attempts,
    int? LastStatusCode,
    string? LastError);

/// <summary>Thin helpers over the public HTTP API, so tests read like a client would use it.</summary>
public static class ApiClientExtensions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<CreatedSubscription> CreateSubscriptionAsync(
        this HttpClient api,
        string[] eventTypes,
        Uri webhookUrl,
        Dictionary<string, string>? filter = null,
        string? secret = null)
    {
        using var response = await api.PostAsJsonAsync("/subscriptions", new
        {
            name = $"test-{Guid.NewGuid():N}",
            eventTypes,
            filter,
            webhookUrl,
            secret,
        }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        return new CreatedSubscription(
            body.GetProperty("subscription").GetProperty("id").GetGuid(),
            body.GetProperty("secret").GetString()!);
    }

    public static async Task<HttpResponseMessage> PublishEventAsync(
        this HttpClient api,
        string type,
        Guid? id = null,
        Dictionary<string, string>? attributes = null,
        object? data = null,
        string source = "integration-tests") =>
        await api.PostAsJsonAsync("/events", new { id, type, source, attributes, data }, Ct);

    public static async Task<Guid> PublishAcceptedEventAsync(
        this HttpClient api,
        string type,
        Dictionary<string, string>? attributes = null,
        object? data = null)
    {
        using var response = await api.PublishEventAsync(type, attributes: attributes, data: data);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        return body.GetProperty("id").GetGuid();
    }

    public static async Task<DeliveryView[]> GetDeliveriesAsync(this HttpClient api, Guid eventId) =>
        await api.GetFromJsonAsync<DeliveryView[]>($"/events/{eventId}/deliveries", Json, Ct) ?? [];

    /// <summary>A unique event type prefix per test, e.g. <c>it3f2a....</c>.</summary>
    public static string UniqueTypePrefix() => $"it{Guid.NewGuid():N}";
}
