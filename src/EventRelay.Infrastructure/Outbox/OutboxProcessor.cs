using Azure.Messaging.ServiceBus;
using EventRelay.Infrastructure.Messaging;
using EventRelay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventRelay.Infrastructure.Outbox;

/// <summary>
/// Publishes one batch of outbox messages. Rows are locked with <c>FOR UPDATE SKIP LOCKED</c>,
/// so several API replicas can publish concurrently without sending the same row twice in
/// the normal case. If the process dies between "sent" and "commit", the batch is sent again
/// later: delivery to the broker is at-least-once and consumers de-duplicate by MessageId.
/// </summary>
public sealed partial class OutboxProcessor(
    EventRelayDbContext db,
    IEventPublisher publisher,
    TimeProvider time,
    ILogger<OutboxProcessor> logger)
{
    /// <returns>Number of messages published; 0 when the outbox is empty or publishing failed.</returns>
    public async Task<int> PublishBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE processed_at IS NULL
                ORDER BY id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            return 0;
        }

        try
        {
            await publisher.PublishAsync(batch, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (exception is ServiceBusException { IsTransient: true } or ServiceBusException { Reason: ServiceBusFailureReason.MessagingEntityNotFound })
            {
                // Broker down or still starting: expected, no stack trace needed.
                LogBrokerUnavailable(batch.Count, exception.Message.Split('\n')[0]);
            }
            else
            {
                LogPublishFailed(exception, batch.Count);
            }

            foreach (var message in batch)
            {
                message.RecordFailure(exception.Message);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        var now = time.GetUtcNow();
        foreach (var message in batch)
        {
            message.MarkPublished(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        LogPublished(batch.Count);
        return batch.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Published {Count} outbox message(s) to Service Bus")]
    private partial void LogPublished(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing {Count} outbox message(s) failed; they stay in the outbox and will be retried")]
    private partial void LogPublishFailed(Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Service Bus unavailable ({Detail}); {Count} message(s) stay in the outbox and will be retried")]
    private partial void LogBrokerUnavailable(int count, string detail);
}
