using Azure.Messaging.ServiceBus;
using EventRelay.Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace EventRelay.Dispatcher;

/// <summary>
/// Reads the <c>events</c> topic and hands every message to <see cref="EventFanOut"/>.
/// Messages are settled explicitly:
/// <list type="bullet">
///   <item>handled or duplicate → complete;</item>
///   <item>malformed or unknown event ("poison") → dead-letter right away, retrying cannot fix it;</item>
///   <item>unexpected error → abandon after a short backoff; after MaxDeliveryCount attempts
///   Service Bus moves the message to the dead-letter queue by itself.</item>
/// </list>
/// </summary>
public sealed partial class EventConsumer(
    ServiceBusClient client,
    IOptions<ServiceBusOptions> options,
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<EventConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan MaxAbandonDelay = TimeSpan.FromSeconds(30);

    private ServiceBusProcessor? processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        processor = client.CreateProcessor(settings.TopicName, settings.SubscriptionName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = settings.MaxConcurrentCalls,
            PrefetchCount = settings.MaxConcurrentCalls * 2,
        });

        processor.ProcessMessageAsync += HandleMessageAsync;
        processor.ProcessErrorAsync += HandleErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);
        LogStarted(settings.TopicName, settings.SubscriptionName);

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (processor is not null)
        {
            await processor.StopProcessingAsync(cancellationToken);
            await processor.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;

        if (!EventEnvelope.TryParse(message.Body, out var envelope, out var parseError))
        {
            LogDeadLettered(message.MessageId, "InvalidPayload", parseError);
            await args.DeadLetterMessageAsync(message, "InvalidPayload", parseError, args.CancellationToken);
            return;
        }

        FanOutResult result;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var fanOut = scope.ServiceProvider.GetRequiredService<EventFanOut>();
            result = await fanOut.HandleAsync(envelope.Id, message.MessageId, args.CancellationToken);
        }
        catch (Exception exception) when (!args.CancellationToken.IsCancellationRequested)
        {
            // Most likely transient (database restart, network). Abandoning makes the message
            // visible again immediately, so wait a little first to avoid burning through
            // MaxDeliveryCount during a short outage.
            var delay = TimeSpan.FromSeconds(Math.Min(MaxAbandonDelay.TotalSeconds, Math.Pow(2, message.DeliveryCount)));
            LogHandlingFailed(exception, message.MessageId, message.DeliveryCount, delay);
            await Task.Delay(delay, time, args.CancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await args.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
            return;
        }

        switch (result.Outcome)
        {
            case FanOutOutcome.UnknownEvent:
                LogDeadLettered(message.MessageId, "UnknownEvent", $"Event {envelope.Id} does not exist.");
                await args.DeadLetterMessageAsync(message, "UnknownEvent", $"Event {envelope.Id} does not exist.", args.CancellationToken);
                return;

            case FanOutOutcome.Duplicate:
                LogDuplicate(message.MessageId);
                break;

            default:
                LogDispatched(envelope.Id, result.EventType!, result.DeliveriesCreated);
                break;
        }

        await args.CompleteMessageAsync(message, args.CancellationToken);
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        // Connection problems (e.g. the emulator still starting) land here and the processor
        // keeps retrying on its own, so transient broker errors get a one-line warning.
        if (args.Exception is ServiceBusException { IsTransient: true } or ServiceBusException { Reason: ServiceBusFailureReason.MessagingEntityNotFound })
        {
            var exception = (ServiceBusException)args.Exception;
            LogBrokerUnavailable(args.EntityPath, exception.Reason.ToString(), exception.Message.Split('\n')[0]);
        }
        else
        {
            LogProcessorError(args.Exception, args.ErrorSource.ToString(), args.EntityPath);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening on topic '{Topic}', subscription '{Subscription}'")]
    private partial void LogStarted(string topic, string subscription);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event {EventId} ({EventType}) → {Count} delivery(ies) scheduled")]
    private partial void LogDispatched(Guid eventId, string eventType, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId} was already processed; acknowledged without side effects")]
    private partial void LogDuplicate(string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message {MessageId} dead-lettered: {Reason} ({Description})")]
    private partial void LogDeadLettered(string messageId, string reason, string description);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling message {MessageId} failed (delivery count {DeliveryCount}); abandoning after {Delay}")]
    private partial void LogHandlingFailed(Exception exception, string messageId, int deliveryCount, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Service Bus not available for {EntityPath} yet ({Reason}): {Detail}. Retrying")]
    private partial void LogBrokerUnavailable(string entityPath, string reason, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Service Bus processor error ({Source}) on {EntityPath}")]
    private partial void LogProcessorError(Exception exception, string source, string entityPath);
}
