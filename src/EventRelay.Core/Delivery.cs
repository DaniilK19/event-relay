namespace EventRelay.Core;

public enum DeliveryStatus
{
    /// <summary>Waiting for the first attempt or for a retry.</summary>
    Pending,

    /// <summary>The webhook answered with a 2xx status.</summary>
    Succeeded,

    /// <summary>Gave up: attempts exhausted or the delivery can never succeed.</summary>
    Failed,
}

/// <summary>
/// Delivery of one event to one subscription. The pair (EventId, SubscriptionId)
/// is unique, which makes fan-out idempotent.
/// </summary>
public sealed class Delivery
{
    public const int MaxErrorLength = 500;

    private Delivery()
    {
    }

    public Guid Id { get; private set; }

    public Guid EventId { get; private set; }

    public Guid SubscriptionId { get; private set; }

    public DeliveryStatus Status { get; private set; }

    /// <summary>Attempts started so far, including one that may be in flight.</summary>
    public int Attempts { get; private set; }

    /// <summary>When the delivery becomes due; null once it reached a final status.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>HTTP status of the last attempt, if the webhook answered at all.</summary>
    public int? LastStatusCode { get; private set; }

    /// <summary>
    /// Short, relay-generated reason for the last failure. Never contains the
    /// subscriber's response body (see docs/adr/003-webhook-allowlist.md).
    /// </summary>
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static Delivery Create(Guid eventId, Guid subscriptionId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        EventId = eventId,
        SubscriptionId = subscriptionId,
        Status = DeliveryStatus.Pending,
        Attempts = 0,
        NextAttemptAt = now.ToUniversalTime(),
        CreatedAt = now.ToUniversalTime(),
    };

    /// <summary>
    /// Starts an attempt and leases the delivery until <c>now + lease</c>: if the worker dies
    /// mid-attempt, the delivery simply becomes due again. The delivery worker performs the
    /// same transition in a single SQL statement to claim rows atomically.
    /// </summary>
    public void BeginAttempt(DateTimeOffset now, TimeSpan lease)
    {
        EnsurePending();
        Attempts++;
        LastAttemptAt = now.ToUniversalTime();
        NextAttemptAt = now.ToUniversalTime() + lease;
    }

    public void RecordSuccess(DateTimeOffset now, int statusCode)
    {
        EnsurePending();
        Status = DeliveryStatus.Succeeded;
        LastStatusCode = statusCode;
        LastError = null;
        NextAttemptAt = null;
        CompletedAt = now.ToUniversalTime();
    }

    /// <summary>Schedules a retry, or fails the delivery when the policy has no attempts left.</summary>
    public void RecordFailure(DateTimeOffset now, int? statusCode, string error, RetryPolicy policy, double randomSample = 0.5)
    {
        EnsurePending();
        LastStatusCode = statusCode;
        LastError = Truncate(error);

        if (policy.CanRetry(Attempts))
        {
            NextAttemptAt = now.ToUniversalTime() + policy.GetDelay(Math.Max(Attempts, 1), randomSample);
            return;
        }

        Status = DeliveryStatus.Failed;
        NextAttemptAt = null;
        CompletedAt = now.ToUniversalTime();
    }

    /// <summary>Fails immediately, without retries, e.g. when the target host is not allowed.</summary>
    public void Abandon(DateTimeOffset now, string reason)
    {
        EnsurePending();
        Status = DeliveryStatus.Failed;
        LastError = Truncate(reason);
        NextAttemptAt = null;
        CompletedAt = now.ToUniversalTime();
    }

    private void EnsurePending()
    {
        if (Status != DeliveryStatus.Pending)
        {
            throw new InvalidOperationException($"Delivery {Id} is already {Status}.");
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
