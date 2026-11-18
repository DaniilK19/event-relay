namespace EventRelay.IntegrationTests.Infrastructure;

/// <summary>Polls until an asynchronous condition holds. The system under test is eventually consistent by design.</summary>
public static class Eventually
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task<T> Until<T>(Func<Task<T>> probe, Func<T, bool> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var last = await probe();

        while (!condition(last))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting until {because}. Last value: {last}");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
            last = await probe();
        }

        return last;
    }

    public static Task<T> Until<T>(Func<T> probe, Func<T, bool> condition, string because, TimeSpan? timeout = null) =>
        Until(() => Task.FromResult(probe()), condition, because, timeout);
}
