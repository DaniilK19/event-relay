namespace EventRelay.Core;

/// <summary>
/// Exponential backoff with a cap and symmetric jitter:
/// <c>delay(n) = min(base * 2^(n-1), max) * (1 ± jitter)</c>, never above <c>max</c>.
/// </summary>
public sealed class RetryPolicy
{
    public RetryPolicy(int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay, double jitterRatio = 0.2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, baseDelay);
        ArgumentOutOfRangeException.ThrowIfNegative(jitterRatio);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitterRatio, 1.0);

        MaxAttempts = maxAttempts;
        BaseDelay = baseDelay;
        MaxDelay = maxDelay;
        JitterRatio = jitterRatio;
    }

    /// <summary>8 attempts: 5s, 10s, 20s ... spread over roughly 10 minutes.</summary>
    public static RetryPolicy Default { get; } = new(8, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));

    /// <summary>Total number of attempts, including the first one.</summary>
    public int MaxAttempts { get; }

    public TimeSpan BaseDelay { get; }

    public TimeSpan MaxDelay { get; }

    public double JitterRatio { get; }

    /// <summary>Whether another attempt is allowed after <paramref name="attemptsMade"/> attempts.</summary>
    public bool CanRetry(int attemptsMade) => attemptsMade < MaxAttempts;

    /// <summary>Delay before the next attempt after <paramref name="attemptsMade"/> failed attempts.</summary>
    /// <param name="attemptsMade">Attempts made so far, starting at 1.</param>
    /// <param name="randomSample">A value in [0, 1]; 0.5 means no jitter. Injected to keep the policy deterministic in tests.</param>
    public TimeSpan GetDelay(int attemptsMade, double randomSample = 0.5)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsMade, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(randomSample);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(randomSample, 1.0);

        var exponent = Math.Min(attemptsMade - 1, 30); // 2^30 * any sane base is already past the cap
        var exponential = BaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(exponential, MaxDelay.TotalMilliseconds);
        var jitterFactor = 1 + JitterRatio * (2 * randomSample - 1); // [1 - jitter, 1 + jitter]

        return TimeSpan.FromMilliseconds(Math.Min(capped * jitterFactor, MaxDelay.TotalMilliseconds));
    }
}
