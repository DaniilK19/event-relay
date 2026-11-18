using Azure.Messaging.ServiceBus;
using EventRelay.Infrastructure.Outbox;
using Microsoft.Extensions.Options;

namespace EventRelay.Infrastructure.Messaging;

public interface IEventPublisher
{
    Task PublishAsync(IReadOnlyCollection<OutboxMessage> messages, CancellationToken cancellationToken);
}

internal sealed class ServiceBusEventPublisher(ServiceBusClient client, IOptions<ServiceBusOptions> options)
    : IEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusSender sender = client.CreateSender(options.Value.TopicName);

    public async Task PublishAsync(IReadOnlyCollection<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        var pending = new Queue<ServiceBusMessage>(messages.Select(ToServiceBusMessage));

        while (pending.Count > 0)
        {
            using var batch = await sender.CreateMessageBatchAsync(cancellationToken);

            while (pending.Count > 0 && batch.TryAddMessage(pending.Peek()))
            {
                pending.Dequeue();
            }

            if (batch.Count == 0)
            {
                // The API caps payload size well below the broker limit, so this means a bug.
                throw new InvalidOperationException(
                    $"Message {pending.Peek().MessageId} does not fit into an empty Service Bus batch.");
            }

            await sender.SendMessagesAsync(batch, cancellationToken);
        }
    }

    public ValueTask DisposeAsync() => sender.DisposeAsync();

    internal static ServiceBusMessage ToServiceBusMessage(OutboxMessage message) =>
        new(BinaryData.FromString(message.Payload))
        {
            MessageId = message.MessageId.ToString(),
            Subject = message.Type,
            ContentType = "application/json",
            ApplicationProperties = { ["eventType"] = message.Type },
        };
}
