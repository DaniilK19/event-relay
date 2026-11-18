namespace EventRelay.Core.Tests;

public class RetryPolicyTests
{
    private static readonly RetryPolicy Policy = new(
        maxAttempts: 5,
        baseDelay: TimeSpan.FromSeconds(10),
        maxDelay: TimeSpan.FromMinutes(1),
        jitterRatio: 0.2);

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 60)] // 80s capped at 60s
    [InlineData(50, 60)] // large exponents stay capped and do not overflow
    public void Delay_doubles_until_the_cap_without_jitter(int attemptsMade, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Policy.GetDelay(attemptsMade, randomSample: 0.5));
    }

    [Fact]
    public void Jitter_stays_within_the_configured_ratio()
    {
        Assert.Equal(TimeSpan.FromSeconds(16), Policy.GetDelay(2, randomSample: 0.0)); // 20s - 20%
        Assert.Equal(TimeSpan.FromSeconds(24), Policy.GetDelay(2, randomSample: 1.0)); // 20s + 20%

        var random = new Random(42);
        for (var i = 0; i < 1_000; i++)
        {
            var delay = Policy.GetDelay(2, random.NextDouble());
            Assert.InRange(delay, TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(24));
        }
    }

    [Fact]
    public void Jitter_never_pushes_delay_above_the_cap()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), Policy.GetDelay(10, randomSample: 1.0));
        Assert.Equal(TimeSpan.FromSeconds(48), Policy.GetDelay(10, randomSample: 0.0));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    public void CanRetry_until_max_attempts_are_used(int attemptsMade, bool expected)
    {
        Assert.Equal(expected, Policy.CanRetry(attemptsMade));
    }

    [Fact]
    public void Invalid_configuration_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(3, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), jitterRatio: 1.5));
    }

    [Fact]
    public void GetDelay_rejects_out_of_range_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy.GetDelay(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy.GetDelay(1, randomSample: 1.1));
    }
}
