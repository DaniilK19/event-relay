using EventRelay.Core;
using EventRelay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EventRelay.Dispatcher;

public enum FanOutOutcome
{
    /// <summary>Deliveries were created for the matching subscriptions (possibly none).</summary>
    Dispatched,

    /// <summary>The message was handled before; nothing was changed.</summary>
    Duplicate,

    /// <summary>The referenced event does not exist. Retrying cannot help.</summary>
    UnknownEvent,
}

public readonly record struct FanOutResult(FanOutOutcome Outcome, string? EventType = null, int DeliveriesCreated = 0);

/// <summary>
/// Turns one event message into delivery rows. Everything happens in one transaction together
/// with the processed_messages insert, so a message is either fully handled exactly once or
/// not at all, no matter how often Service Bus delivers it.
/// </summary>
public sealed class EventFanOut(EventRelayDbContext db, TimeProvider time)
{
    public const string ConsumerName = "dispatcher";

    public async Task<FanOutResult> HandleAsync(Guid eventId, string messageId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Idempotent consumer. Concurrent duplicates block on the primary key until the first
        // transaction finishes; if it committed, ON CONFLICT turns this insert into a no-op.
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO processed_messages (consumer, message_id, processed_at)
             VALUES ({ConsumerName}, {messageId}, {now})
             ON CONFLICT DO NOTHING
             """,
            cancellationToken);

        if (inserted == 0)
        {
            return new FanOutResult(FanOutOutcome.Duplicate);
        }

        var evt = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (evt is null)
        {
            // Not committed: the processed_messages row is rolled back with the transaction.
            return new FanOutResult(FanOutOutcome.UnknownEvent);
        }

        // Pre-select candidates in SQL (array overlap, GIN index), decide precisely in Core.
        var candidatePatterns = SubscriptionMatcher.CandidatePatterns(evt.Type).ToArray();
        var candidates = await db.Subscriptions.AsNoTracking()
            .Where(s => s.IsActive && s.EventTypes.Any(t => candidatePatterns.Contains(t)))
            .ToListAsync(cancellationToken);

        var matching = candidates
            .Where(s => SubscriptionMatcher.Matches(s, evt.Type, evt.Attributes))
            .Select(s => s.Id)
            .ToHashSet();

        // The same event could arrive under a different message id (e.g. re-published by hand).
        // Skip pairs that already exist; the unique index on (event_id, subscription_id) backs this up.
        var alreadyPlanned = await db.Deliveries
            .Where(d => d.EventId == evt.Id)
            .Select(d => d.SubscriptionId)
            .ToListAsync(cancellationToken);
        matching.ExceptWith(alreadyPlanned);

        db.Deliveries.AddRange(matching.Select(subscriptionId => Delivery.Create(evt.Id, subscriptionId, now)));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new FanOutResult(FanOutOutcome.Dispatched, evt.Type, matching.Count);
    }
}
