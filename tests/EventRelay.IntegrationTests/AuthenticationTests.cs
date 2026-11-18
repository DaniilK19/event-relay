using System.Net;
using System.Net.Http.Json;
using EventRelay.IntegrationTests.Infrastructure;

namespace EventRelay.IntegrationTests;

[Collection(EventRelayCollection.Name)]
public sealed class AuthenticationTests(EventRelayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("POST", "/events")]
    [InlineData("GET", "/events/0b9f5bb5-61c1-4bd4-8b52-7a1c0c3b5d11")]
    [InlineData("GET", "/subscriptions")]
    [InlineData("POST", "/subscriptions")]
    [InlineData("GET", "/not-a-route")] // secure by default: unknown routes do not reveal anything either
    public async Task Requests_without_an_api_key_are_rejected(string method, string path)
    {
        using var anonymous = fixture.CreateApiClient(apiKey: null);
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };

        using var response = await anonymous.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("X-Api-Key", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task An_unknown_api_key_is_rejected()
    {
        using var client = fixture.CreateApiClient($"not-a-real-key-{Guid.NewGuid():N}");

        using var response = await client.GetAsync("/subscriptions", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Publisher_key_can_publish_events_and_read_their_deliveries()
    {
        using var publisher = fixture.CreateApiClient(EventRelayFixture.PublisherKey);

        var eventId = await publisher.PublishAcceptedEventAsync($"{ApiClientExtensions.UniqueTypePrefix()}.delayed");
        var deliveries = await publisher.GetDeliveriesAsync(eventId);

        Assert.Empty(deliveries);
    }

    [Theory]
    [InlineData("GET", "/subscriptions")]
    [InlineData("POST", "/subscriptions")]
    [InlineData("DELETE", "/subscriptions/0b9f5bb5-61c1-4bd4-8b52-7a1c0c3b5d11")]
    public async Task Publisher_key_cannot_manage_subscriptions(string method, string path)
    {
        using var publisher = fixture.CreateApiClient(EventRelayFixture.PublisherKey);
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new
            {
                name = "publisher-should-not-create-this",
                eventTypes = new[] { "flight.*" },
                webhookUrl = fixture.Webhooks.UrlFor("nope"),
            }),
        };

        using var response = await publisher.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar")]
    public async Task Health_and_api_description_are_public(string path)
    {
        using var anonymous = fixture.CreateApiClient(apiKey: null);

        using var response = await anonymous.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_describes_the_api_key_header()
    {
        using var anonymous = fixture.CreateApiClient(apiKey: null);

        var document = await anonymous.GetStringAsync("/openapi/v1.json", Ct);

        Assert.Contains("\"apiKey\"", document);
        Assert.Contains("X-Api-Key", document);
    }
}
