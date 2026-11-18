using System.Net;
using EventRelay.Infrastructure;
using EventRelay.Infrastructure.Webhooks;
using EventRelay.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventRelay.IntegrationTests;

/// <summary>
/// The connect-time IP check, exercised through the real webhook HttpClient against a local
/// server. Needs no containers: the sender is built from the same DI registration the dispatcher uses.
/// </summary>
public sealed class WebhookConnectionGuardTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestWebhookServer server = new();

    public async ValueTask InitializeAsync() => await server.StartAsync();

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")] // resolved through DNS: ::1 and 127.0.0.1
    public async Task Private_addresses_are_refused_at_connect_time(string host)
    {
        using var services = BuildServices(allowPrivateNetworks: false);
        var hook = server.NewHook();
        var url = new UriBuilder(server.UrlFor(hook)) { Host = host }.Uri;

        var result = await Send(services, url);

        Assert.False(result.Succeeded);
        Assert.True(result.IsPermanent);
        Assert.Equal("address_not_allowed", result.Error);
        Assert.Empty(server.Received(hook));
    }

    [Fact]
    public async Task Private_addresses_are_reachable_when_explicitly_allowed()
    {
        using var services = BuildServices(allowPrivateNetworks: true);
        var hook = server.NewHook();

        var result = await Send(services, server.UrlFor(hook));

        Assert.True(result.Succeeded);
        Assert.Single(server.Received(hook));
    }

    [Fact]
    public async Task Cloud_metadata_endpoint_is_refused_even_when_private_networks_are_allowed()
    {
        using var services = BuildServices(allowPrivateNetworks: true);

        var result = await Send(services, new Uri("http://169.254.169.254/latest/meta-data/"));

        Assert.Equal("address_not_allowed", result.Error);
        Assert.True(result.IsPermanent);
    }

    [Theory]
    [InlineData("8.8.8.8", AddressScope.Public)]
    [InlineData("1.1.1.1", AddressScope.Public)]
    [InlineData("172.32.0.1", AddressScope.Public)] // just outside 172.16.0.0/12
    [InlineData("2606:4700:4700::1111", AddressScope.Public)]
    [InlineData("::ffff:8.8.8.8", AddressScope.Public)]
    [InlineData("64:ff9b::808:808", AddressScope.Public)] // NAT64 form of 8.8.8.8
    [InlineData("127.0.0.1", AddressScope.Private)]
    [InlineData("127.1.2.3", AddressScope.Private)]
    [InlineData("10.0.0.5", AddressScope.Private)]
    [InlineData("172.16.0.1", AddressScope.Private)]
    [InlineData("172.31.255.255", AddressScope.Private)]
    [InlineData("192.168.1.10", AddressScope.Private)]
    [InlineData("100.64.0.1", AddressScope.Private)]
    [InlineData("::1", AddressScope.Private)]
    [InlineData("fd12:3456::1", AddressScope.Private)]
    [InlineData("::ffff:10.0.0.1", AddressScope.Private)] // IPv4-mapped IPv6
    [InlineData("169.254.169.254", AddressScope.Blocked)] // Azure / AWS / GCP metadata
    [InlineData("::ffff:169.254.169.254", AddressScope.Blocked)]
    [InlineData("64:ff9b::a9fe:a9fe", AddressScope.Blocked)] // NAT64 form of 169.254.169.254
    [InlineData("::169.254.169.254", AddressScope.Blocked)] // deprecated IPv4-compatible form
    [InlineData("2002:a9fe:a9fe::1", AddressScope.Blocked)] // 6to4 wrapping 169.254.169.254
    [InlineData("fd00:ec2::254", AddressScope.Blocked)] // AWS metadata over IPv6, inside fc00::/7
    [InlineData("100.100.100.200", AddressScope.Blocked)] // Alibaba metadata, inside 100.64.0.0/10
    [InlineData("168.63.129.16", AddressScope.Blocked)] // Azure platform endpoint, a public address
    [InlineData("::ffff:168.63.129.16", AddressScope.Blocked)]
    [InlineData("64:ff9b::a83f:8110", AddressScope.Blocked)] // NAT64 form of 168.63.129.16
    [InlineData("168.63.129.17", AddressScope.Public)] // only that one address is special
    [InlineData("0.0.0.0", AddressScope.Blocked)]
    [InlineData("224.0.0.1", AddressScope.Blocked)]
    [InlineData("255.255.255.255", AddressScope.Blocked)]
    [InlineData("192.0.2.1", AddressScope.Blocked)]
    [InlineData("::", AddressScope.Blocked)]
    [InlineData("fe80::1", AddressScope.Blocked)]
    [InlineData("ff02::1", AddressScope.Blocked)]
    [InlineData("2001:db8::1", AddressScope.Blocked)]
    public void Addresses_are_classified(string address, AddressScope expected) =>
        Assert.Equal(expected, NetworkAddressRules.Classify(IPAddress.Parse(address)));

    private static ServiceProvider BuildServices(bool allowPrivateNetworks)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The allowlist lets everything below through, so only the IP check decides.
                ["Webhooks:AllowedHosts:0"] = "127.0.0.1",
                ["Webhooks:AllowedHosts:1"] = "localhost",
                ["Webhooks:AllowedHosts:2"] = "169.254.169.254",
                ["Webhooks:RequireHttps"] = "false",
                ["Webhooks:AllowPrivateNetworks"] = allowPrivateNetworks ? "true" : "false",
                ["Webhooks:Timeout"] = "00:00:03",
                ["Webhooks:ConnectTimeout"] = "00:00:02",
            })
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddEventRelayWebhookSender(configuration)
            .BuildServiceProvider();
    }

    private static async Task<WebhookResult> Send(IServiceProvider services, Uri url) =>
        await services.GetRequiredService<WebhookSender>().SendAsync(
            new WebhookRequest(url, "test-secret-0123456789", Guid.NewGuid(), Guid.NewGuid(), "flight.delayed", 1, "{}"u8.ToArray()),
            Ct);
}
