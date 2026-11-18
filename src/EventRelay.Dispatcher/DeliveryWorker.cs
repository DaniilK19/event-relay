using System.Collections.Concurrent;
using EventRelay.Core;
using EventRelay.Infrastructure.Messaging;
using EventRelay.Infrastructure.Persistence;
using EventRelay.Infrastructure.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventRelay.Dispatcher;

/// <summary>
/// Polls for due deliveries, sends the webhooks and records the outcome:
/// success, retry later (exponential backoff) or failed for good.
/// </summary>
public sealed partial class DeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<DeliveryOptions> options,
    TimeProvider time,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    private readonly DeliveryOptions settings = options.Value;
    private readonly RetryPolicy retryPolicy = options.Value.ToRetryPolicy();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var claimed = await ProcessBatchAsync(stoppingToken);
                delay = claimed == settings.BatchSize ? TimeSpan.Zero : settings.PollingInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogUnexpectedError(exception, settings.ErrorDelay);
                delay = settings.ErrorDelay;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventRelayDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<WebhookSender>();

        var claimed = await ClaimDueDeliveriesAsync(db, stoppingToken);
        if (claimed.Count == 0)
        {
            return 0;
        }

        var eventIds = claimed.Select(d => d.EventId).Distinct().ToArray();
        var subscriptionIds = claimed.Select(d => d.SubscriptionId).Distinct().ToArray();
        var events = await db.Events.AsNoTracking()
            .Where(e => eventIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, stoppingToken);
        var subscriptions = await db.Subscriptions.AsNoTracking()
            .Where(s => subscriptionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, stoppingToken);
        var bodies = events.Values.ToDictionary(e => e.Id, e => EventEnvelope.FromEvent(e).ToUtf8Bytes());

        var results = new ConcurrentDictionary<Guid, WebhookResult>();
        try
        {
            await Parallel.ForEachAsync(
                claimed,
                new ParallelOptions { MaxDegreeOfParallelism = settings.MaxParallelism, CancellationToken = stoppingToken },
                async (delivery, token) =>
                {
                    // Missing only if the subscription was deleted a moment ago; its deliveries
                    // are gone too (ON DELETE CASCADE), so there is nothing left to do.
                    if (subscriptions.TryGetValue(delivery.SubscriptionId, out var subscription)
                        && events.TryGetValue(delivery.EventId, out var evt))
                    {
                        results[delivery.Id] = await AttemptAsync(sender, delivery, subscription, evt, bodies[evt.Id], token);
                    }
                });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: record what finished. Unfinished deliveries keep their lease and
            // are picked up again once it expires.
        }

        var now = time.GetUtcNow();
        foreach (var delivery in claimed)
        {
            if (results.TryGetValue(delivery.Id, out var result))
            {
                Apply(delivery, result, now);
            }
        }

        // Even during shutdown: the requests were sent, their outcome must not be lost.
        await SaveOutcomesAsync(db);
        return claimed.Count;
    }

    private async Task<WebhookResult> AttemptAsync(
        WebhookSender sender,
        Delivery delivery,
        Subscription subscription,
        Event evt,
        byte[] body,
        CancellationToken cancellationToken)
    {
        if (!subscription.IsActive)
        {
            return WebhookResult.Rejected("subscription_inactive");
        }

        try
        {
            return await sender.SendAsync(
                new WebhookRequest(
                    new Uri(subscription.WebhookUrl),
                    subscription.Secret,
                    delivery.Id,
                    evt.Id,
                    evt.Type,
                    delivery.Attempts,
                    body),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A bug must not take the whole batch down with it: count it as a failed attempt.
            LogAttemptCrashed(exception, delivery.Id);
            return WebhookResult.Failure(null, "internal_error");
        }
    }

    private static async Task SaveOutcomesAsync(EventRelayDbContext db)
    {
        while (true)
        {
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (DbUpdateConcurrencyException exception)
            {
                // A row vanished while its webhook was in flight (subscription deleted).
                // Drop it and keep the other outcomes.
                foreach (var entry in exception.Entries)
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    /// <summary>
    /// Claims due deliveries in one statement: counts the attempt and moves next_attempt_at
    /// forward by the lease (same transition as <see cref="Delivery.BeginAttempt"/>).
    /// SKIP LOCKED lets several dispatcher instances claim disjoint rows without blocking.
    /// </summary>
    private async Task<List<Delivery>> ClaimDueDeliveriesAsync(EventRelayDbContext db, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var leaseUntil = now + settings.Lease;

        return await db.Deliveries
            .FromSql($"""
                WITH due AS (
                    SELECT id FROM deliveries
                    WHERE status = 'Pending' AND next_attempt_at <= {now}
                    ORDER BY next_attempt_at
                    LIMIT {settings.BatchSize}
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE deliveries AS d
                SET attempts = d.attempts + 1,
                    last_attempt_at = {now},
                    next_attempt_at = {leaseUntil}
                FROM due
                WHERE d.id = due.id
                RETURNING d.*
                """)
            .ToListAsync(cancellationToken);
    }

    private void Apply(Delivery delivery, WebhookResult result, DateTimeOffset now)
    {
        if (result.Succeeded)
        {
            delivery.RecordSuccess(now, result.StatusCode!.Value);
            LogSucceeded(delivery.EventId, delivery.SubscriptionId, result.StatusCode.Value, delivery.Attempts);
            return;
        }

        var error = result.Error ?? "unknown_error";
        if (result.IsPermanent)
        {
            delivery.Abandon(now, error);
            LogFailed(delivery.Id, delivery.Attempts, error);
            return;
        }

        delivery.RecordFailure(now, result.StatusCode, error, retryPolicy, Random.Shared.NextDouble());
        if (delivery.Status == DeliveryStatus.Failed)
        {
            LogFailed(delivery.Id, delivery.Attempts, error);
        }
        else
        {
            LogRetryScheduled(delivery.Id, delivery.Attempts, error, delivery.NextAttemptAt!.Value);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivered event {EventId} to subscription {SubscriptionId}: HTTP {StatusCode} (attempt {Attempt})")]
    private partial void LogSucceeded(Guid eventId, Guid subscriptionId, int statusCode, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery {DeliveryId} attempt {Attempt} failed ({Error}); next attempt at {NextAttemptAt:O}")]
    private partial void LogRetryScheduled(Guid deliveryId, int attempt, string error, DateTimeOffset nextAttemptAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {DeliveryId} failed permanently after {Attempts} attempt(s) ({Error})")]
    private partial void LogFailed(Guid deliveryId, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while attempting delivery {DeliveryId}")]
    private partial void LogAttemptCrashed(Exception exception, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery worker failed; retrying in {Delay}")]
    private partial void LogUnexpectedError(Exception exception, TimeSpan delay);
}
