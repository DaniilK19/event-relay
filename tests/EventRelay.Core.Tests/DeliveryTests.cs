namespace EventRelay.Core.Tests;

public class DeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);
    private static readonly RetryPolicy Policy = new(3, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), jitterRatio: 0);

    [Fact]
    public void New_delivery_is_pending_and_due_immediately()
    {
        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.Attempts);
        Assert.Equal(Now, delivery.NextAttemptAt);
    }

    [Fact]
    public void BeginAttempt_counts_the_attempt_and_leases_the_delivery()
    {
        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

        delivery.BeginAttempt(Now, Lease);

        Assert.Equal(1, delivery.Attempts);
        Assert.Equal(Now, delivery.LastAttemptAt);
        Assert.Equal(Now + Lease, delivery.NextAttemptAt);
    }

    [Fact]
    public void Success_completes_the_delivery()
    {
        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), Now);
        delivery.BeginAttempt(Now, Lease);

        delivery.RecordSuccess(Now.AddSeconds(1), 204);

        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
        Assert.Equal(204, delivery.LastStatusCode);
        Assert.Null(delivery.NextAttemptAt);
        Assert.Equal(Now.AddSeconds(1), delivery.CompletedAt);
    }

    [Fact]
    public void Failure_schedules_a_retry_with_backoff()
    {
        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

        delivery.BeginAttempt(Now, Lease);
        delivery.RecordFailure(Now, 500, "http_500", Policy);

        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(Now.AddSeconds(10), delivery.NextAttemptAt);

        delivery.BeginAttempt(Now.AddSeconds(10), Lease);
        delivery.RecordFailure(Now.AddSeconds(10), null, "timeout", Policy);

        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(Now.AddSeconds(30), delivery.NextAttemptAt);
        Assert.Null(delivery.LastStatusCode);
        Assert.Equal("timeout", delivery.LastError);
    }

    [Fact]
    public void Failure_on_the_last_attempt_fails_the_delivery()
    {
        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

        for (var attempt = 1; attempt <= Policy.MaxAttempts; attempt++)
        {
            delivery.BeginAttempt(Now, Lease);
            delivery.RecordFailure(Now, 503, "http_503", Policy);
        }

        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
        Assert.Equal(3, delivery.Attempts);
        Assert.Null(delivery.NextAttemptAt);
        Assert.NotNull(delivery.CompletedAt);
    }

    [Fact]
    public void Abandon_fails_without_retrying()
    {
        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

        delivery.Abandon(Now, "host_not_allowed");

        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
        Assert.Equal("host_not_allowed", delivery.LastError);
        Assert.Null(delivery.NextAttemptAt);
    }

    [Fact]
    public void Completed_delivery_cannot_change_again()
    {
        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), Now);
        delivery.BeginAttempt(Now, Lease);
        delivery.RecordSuccess(Now, 200);

        Assert.Throws<InvalidOperationException>(() => delivery.BeginAttempt(Now, Lease));
        Assert.Throws<InvalidOperationException>(() => delivery.RecordFailure(Now, 500, "http_500", Policy));
        Assert.Throws<InvalidOperationException>(() => delivery.Abandon(Now, "late"));
    }

    [Fact]
    public void Long_errors_are_truncated()
    {
        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), Now);
        delivery.BeginAttempt(Now, Lease);

        delivery.RecordFailure(Now, null, new string('x', 2_000), Policy);

        Assert.Equal(Delivery.MaxErrorLength, delivery.LastError!.Length);
    }
}
