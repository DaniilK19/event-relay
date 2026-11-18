using System.Net;
using System.Net.Http.Json;
using EventRelay.Core;
using EventRelay.IntegrationTests.Infrastructure;

namespace EventRelay.IntegrationTests;

[Collection(EventRelayCollection.Name)]
public sealed class AllowlistTests(EventRelayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HttpClient api = fixture.Api;

    [Theory]
    [InlineData("http://evil.example.com/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // cloud metadata endpoint
    [InlineData("http://localhost:5432/")] // allowlist contains 127.0.0.1, not localhost
    [InlineData("http://user:password@127.0.0.1/hook")]
    [InlineData("ftp://127.0.0.1/hook")]
    public async Task Subscription_to_a_host_outside_the_allowlist_is_rejected(string url)
    {
        using var response = await api.PostAsJsonAsync("/subscriptions", new
        {
            name = "should-fail",
            eventTypes = new[] { "flight.*" },
            webhookUrl = url,
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("webhookUrl", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Delivery_to_a_host_no_longer_allowed_fails_without_sending()
    {
        // A subscription saved while the host was still allowed (inserted directly, bypassing
        // the API check). The worker re-checks the allowlist before every attempt.
        var type = $"{ApiClientExtensions.UniqueTypePrefix()}.delayed";
        await using (var db = fixture.CreateDbContext())
        {
            db.Subscriptions.Add(Subscription.Create(
                "legacy", [type], null, new Uri("http://no-longer-allowed.test/hook"),
                Subscription.GenerateSecret(), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Ct);
        }

        var eventId = await api.PublishAcceptedEventAsync(type);

        var deliveries = await Eventually.Until(
            () => api.GetDeliveriesAsync(eventId),
            d => d is [{ Status: DeliveryStatus.Failed }],
            "the delivery is abandoned");

        Assert.Equal("host_not_allowed", deliveries[0].LastError);
        Assert.Null(deliveries[0].LastStatusCode);
        Assert.Equal(1, deliveries[0].Attempts);
    }

    [Fact]
    public async Task Secret_is_returned_on_creation_only()
    {
        var created = await api.CreateSubscriptionAsync(["flight.*"], fixture.Webhooks.UrlFor(fixture.Webhooks.NewHook()));

        Assert.StartsWith("whsec_", created.Secret);
        var read = await api.GetStringAsync($"/subscriptions/{created.Id}", Ct);
        Assert.DoesNotContain("secret", read, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(created.Secret, read);
    }
}
