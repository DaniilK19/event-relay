using System.Globalization;
using System.Net;
using System.Text.Json;
using EventRelay.Core;
using EventRelay.Infrastructure.Webhooks;
using EventRelay.IntegrationTests.Infrastructure;

namespace EventRelay.IntegrationTests;

[Collection(EventRelayCollection.Name)]
public sealed class DeliveryTests(EventRelayFixture fixture)
{
    private readonly HttpClient api = fixture.Api;
    private readonly TestWebhookServer webhooks = fixture.Webhooks;

    [Fact]
    public async Task Event_reaches_the_webhook_signed_with_the_subscription_secret()
    {
        var type = $"{ApiClientExtensions.UniqueTypePrefix()}.delayed";
        var hook = webhooks.NewHook();
        var subscription = await api.CreateSubscriptionAsync([type], webhooks.UrlFor(hook));

        var eventId = await api.PublishAcceptedEventAsync(
            type,
            attributes: new() { ["airline"] = "CT" },
            data: new { delayMinutes = 25 });

        var received = await Eventually.Until(() => webhooks.Received(hook), r => r.Count == 1, "the webhook received the event");
        var request = received[0];

        Assert.Equal(eventId.ToString(), request.Header(WebhookSender.EventIdHeader));
        Assert.Equal(type, request.Header(WebhookSender.EventTypeHeader));
        Assert.Equal("1", request.Header(WebhookSender.AttemptHeader));

        var timestamp = long.Parse(request.Header(WebhookSender.TimestampHeader), CultureInfo.InvariantCulture);
        Assert.Equal(
            WebhookSignature.Compute(subscription.Secret, timestamp, request.Body),
            request.Header(WebhookSender.SignatureHeader));

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(eventId, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("CT", body.RootElement.GetProperty("attributes").GetProperty("airline").GetString());
        Assert.Equal(25, body.RootElement.GetProperty("data").GetProperty("delayMinutes").GetInt32());

        var deliveries = await Eventually.Until(
            () => api.GetDeliveriesAsync(eventId),
            d => d is [{ Status: DeliveryStatus.Succeeded }],
            "the delivery is recorded as succeeded");
        Assert.Equal(1, deliveries[0].Attempts);
        Assert.Equal(200, deliveries[0].LastStatusCode);
    }

    [Fact]
    public async Task Failed_attempts_are_retried_until_the_webhook_succeeds()
    {
        var type = $"{ApiClientExtensions.UniqueTypePrefix()}.gate_changed";
        var hook = webhooks.NewHook();
        webhooks.Respond(hook, request => request < 3 ? 500 : 200);
        await api.CreateSubscriptionAsync([type], webhooks.UrlFor(hook));

        var eventId = await api.PublishAcceptedEventAsync(type);

        var deliveries = await Eventually.Until(
            () => api.GetDeliveriesAsync(eventId),
            d => d is [{ Status: DeliveryStatus.Succeeded }],
            "the third attempt succeeds");

        Assert.Equal(3, deliveries[0].Attempts);
        var received = webhooks.Received(hook);
        Assert.Equal(["1", "2", "3"], received.Select(r => r.Header(WebhookSender.AttemptHeader)));
        Assert.All(received, r => Assert.Equal(eventId.ToString(), r.Header(WebhookSender.EventIdHeader)));
    }

    [Fact]
    public async Task Delivery_fails_after_max_attempts()
    {
        var type = $"{ApiClientExtensions.UniqueTypePrefix()}.cancelled";
        var hook = webhooks.NewHook();
        webhooks.Respond(hook, _ => 503);
        await api.CreateSubscriptionAsync([type], webhooks.UrlFor(hook));

        var eventId = await api.PublishAcceptedEventAsync(type);

        var deliveries = await Eventually.Until(
            () => api.GetDeliveriesAsync(eventId),
            d => d is [{ Status: DeliveryStatus.Failed }],
            "the delivery gives up");

        Assert.Equal(EventRelayFixture.MaxAttempts, deliveries[0].Attempts);
        Assert.Equal(503, deliveries[0].LastStatusCode);
        Assert.Equal("http_503", deliveries[0].LastError);
        Assert.Equal(EventRelayFixture.MaxAttempts, webhooks.Received(hook).Count);
    }

    [Fact]
    public async Task Redirects_are_not_followed_and_count_as_failures()
    {
        var type = $"{ApiClientExtensions.UniqueTypePrefix()}.delayed";
        var hook = webhooks.NewHook();
        webhooks.Respond(hook, _ => 302);
        await api.CreateSubscriptionAsync([type], webhooks.UrlFor(hook));

        var eventId = await api.PublishAcceptedEventAsync(type);

        var deliveries = await Eventually.Until(
            () => api.GetDeliveriesAsync(eventId),
            d => d is [{ Status: DeliveryStatus.Failed }],
            "the redirecting webhook is treated as failing");
        Assert.Equal("http_302", deliveries[0].LastError);
    }

    [Fact]
    public async Task Subscriptions_are_selected_by_type_pattern_and_attribute_filter()
    {
        var prefix = ApiClientExtensions.UniqueTypePrefix();
        var (wildcardCt, exactCt, wildcardFb) = (webhooks.NewHook(), webhooks.NewHook(), webhooks.NewHook());

        await api.CreateSubscriptionAsync([$"{prefix}.*"], webhooks.UrlFor(wildcardCt), new() { ["airline"] = "CT" });
        await api.CreateSubscriptionAsync([$"{prefix}.cancelled"], webhooks.UrlFor(exactCt), new() { ["airline"] = "CT" });
        await api.CreateSubscriptionAsync([$"{prefix}.*"], webhooks.UrlFor(wildcardFb), new() { ["airline"] = "FB" });

        var eventId = await api.PublishAcceptedEventAsync($"{prefix}.delayed", attributes: new() { ["airline"] = "CT" });

        await Eventually.Until(() => api.GetDeliveriesAsync(eventId), d => d is [{ Status: DeliveryStatus.Succeeded }], "the only match is delivered");
        Assert.Single(webhooks.Received(wildcardCt));
        Assert.Empty(webhooks.Received(exactCt));
        Assert.Empty(webhooks.Received(wildcardFb));
    }

    [Fact]
    public async Task Event_without_matching_subscriptions_creates_no_deliveries()
    {
        var eventId = await api.PublishAcceptedEventAsync($"{ApiClientExtensions.UniqueTypePrefix()}.unrouted");

        await using var db = fixture.CreateDbContext();
        await Eventually.Until(
            () => Task.FromResult(db.ProcessedMessages.Any(m => m.MessageId == eventId.ToString())),
            processed => processed,
            "the dispatcher has handled the event");

        Assert.Empty(await api.GetDeliveriesAsync(eventId));
    }

    [Fact]
    public async Task Invalid_event_is_rejected_with_field_errors()
    {
        using var response = await api.PublishEventAsync("Not A Type", source: "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"type\"", problem);
        Assert.Contains("\"source\"", problem);
    }
}
