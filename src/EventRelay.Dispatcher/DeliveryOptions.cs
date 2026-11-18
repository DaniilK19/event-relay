using EventRelay.Core;

namespace EventRelay.Dispatcher;

public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";

    /// <summary>Deliveries claimed per polling round.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Concurrent webhook requests per dispatcher instance.</summary>
    public int MaxParallelism { get; set; } = 8;

    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Pause after an unexpected error (e.g. database unavailable).</summary>
    public TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a claimed delivery is reserved for this instance. Must comfortably exceed the
    /// webhook timeout; if the instance dies, the delivery becomes due again after the lease.
    /// </summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Total attempts per delivery before it is marked failed.</summary>
    public int MaxAttempts { get; set; } = 8;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    public double RetryJitter { get; set; } = 0.2;

    public RetryPolicy ToRetryPolicy() => new(MaxAttempts, RetryBaseDelay, RetryMaxDelay, RetryJitter);
}
