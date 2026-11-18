using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using EventRelay.Core;
using EventRelay.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EventRelay.IntegrationTests;

[Collection(EventRelayCollection.Name)]
public sealed class IdempotencyTests(EventRelayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HttpClient api = fixture.Api;
    private readonly TestWebhookServer webhooks = fixture.Webhooks;

    [Fact]
    public async Task Same_event_posted_twice_is_accepted_once_and_delivered_once()
    {
        var type = $"{ApiClientExtensions.UniqueTypePrefix()}.delayed";
        var hook = webhooks.NewHook();
        await api.CreateSubscriptionAsync([type], webhooks.UrlFor(hook));
        var eventId = Guid.NewGuid();

        using var first = await api.PublishEventAsync(type, eventId);
        using var second = await api.PublishEventAsync(type, eventId);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>(ApiClientExtensions.Json, Ct);
        Assert.True(body.GetProperty("duplicate").GetBoolean());

        await Eventually.Until(() => webhooks.Received(hook), r => r.Count == 1, "the event is delivered");
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        Assert.Single(webhooks.Received(hook));
    }

    [Fact]
    public async Task Reusing_an_event_id_for_a_different_event_is_a_conflict()
    {
        var prefix = ApiClientExtensions.UniqueTypePrefix();
        var eventId = Guid.NewGuid();

        using var first = await api.PublishEventAsync($"{prefix}.delayed", eventId);
        using var second = await api.PublishEventAsync($"{prefix}.cancelled", eventId);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Redelivered_broker_message_does_not_produce_a_second_webhook()
    {
        var type = $"{ApiClientExtensions.UniqueTypePrefix()}.gate_changed";
        var hook = webhooks.NewHook();
        await api.CreateSubscriptionAsync([type], webhooks.UrlFor(hook));
        var eventId = await api.PublishAcceptedEventAsync(type);
        await Eventually.Until(() => webhooks.Received(hook), r => r.Count == 1, "the event is delivered");

        // Simulate at-least-once delivery: the outbox crashed after sending but before marking
        // the row as published, so the same message (same MessageId) reaches the topic again.
        // Then the same event once more under a different MessageId (manual re-publish).
        var envelope = BinaryData.FromObjectAsJson(new { id = eventId, type, source = "integration-tests", time = DateTimeOffset.UtcNow, attributes = new { } });
        var replayId = $"replay-{Guid.NewGuid():N}";
        await using (var sender = fixture.ServiceBus.CreateSender("events"))
        {
            await sender.SendMessageAsync(new ServiceBusMessage(envelope) { MessageId = eventId.ToString() }, Ct);
            await sender.SendMessageAsync(new ServiceBusMessage(envelope) { MessageId = replayId }, Ct);
        }

        await using var db = fixture.CreateDbContext();
        await Eventually.Until(
            () => db.ProcessedMessages.AnyAsync(m => m.MessageId == replayId, Ct),
            processed => processed,
            "the dispatcher has handled the replayed message");
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        Assert.Single(webhooks.Received(hook));
        Assert.Equal(1, await db.Deliveries.CountAsync(d => d.EventId == eventId, Ct));
    }

    [Fact]
    public async Task Malformed_message_is_dead_lettered()
    {
        var messageId = $"poison-{Guid.NewGuid():N}";
        await using (var sender = fixture.ServiceBus.CreateSender("events"))
        {
            await sender.SendMessageAsync(new ServiceBusMessage("this is not json") { MessageId = messageId }, Ct);
        }

        var deadLettered = await ReceiveDeadLetterAsync(messageId);

        Assert.Equal("InvalidPayload", deadLettered.DeadLetterReason);
    }

    [Fact]
    public async Task Message_about_an_unknown_event_is_dead_lettered()
    {
        var unknownId = Guid.NewGuid();
        var body = BinaryData.FromObjectAsJson(new { id = unknownId, type = "ghost.event", source = "nowhere", time = DateTimeOffset.UtcNow });
        await using (var sender = fixture.ServiceBus.CreateSender("events"))
        {
            await sender.SendMessageAsync(new ServiceBusMessage(body) { MessageId = unknownId.ToString() }, Ct);
        }

        var deadLettered = await ReceiveDeadLetterAsync(unknownId.ToString());

        Assert.Equal("UnknownEvent", deadLettered.DeadLetterReason);
    }

    private async Task<ServiceBusReceivedMessage> ReceiveDeadLetterAsync(string messageId)
    {
        // Peek: inspect the dead-letter queue without locking or removing anything,
        // so tests cannot interfere with each other's messages.
        await using var receiver = fixture.ServiceBus.CreateReceiver(
            "events", "dispatcher", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

        var messages = await Eventually.Until(
            () => receiver.PeekMessagesAsync(maxMessages: 250, fromSequenceNumber: 0, Ct),
            peeked => peeked.Any(m => m.MessageId == messageId),
            $"message {messageId} reaches the dead-letter queue");

        return messages.First(m => m.MessageId == messageId);
    }
}
