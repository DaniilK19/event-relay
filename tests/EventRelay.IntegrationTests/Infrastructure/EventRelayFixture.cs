using Azure.Messaging.ServiceBus;
using EventRelay.Dispatcher;
using EventRelay.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.ServiceBus;

namespace EventRelay.IntegrationTests.Infrastructure;

/// <summary>
/// The whole system for one test run: PostgreSQL and the Service Bus emulator in containers
/// (Testcontainers), the API in-memory via WebApplicationFactory (including its outbox
/// publisher), the dispatcher as an in-process generic host, and a real HTTP webhook target.
/// Started once and shared by all tests in the collection; tests isolate themselves by
/// using unique event types and hook names.
/// </summary>
public sealed class EventRelayFixture : IAsyncLifetime
{
    public const int MaxAttempts = 3;

    // Generated per run; only ever used against the in-memory test server.
    public static readonly string AdminKey = $"test-admin-{Guid.NewGuid():N}";
    public static readonly string PublisherKey = $"test-publisher-{Guid.NewGuid():N}";

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private readonly ServiceBusContainer serviceBus =
        new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.1")
            .WithAcceptLicenseAgreement(true)
            .WithConfig(Path.Combine(AppContext.BaseDirectory, "servicebus-emulator.config.json"))
            .Build();

    private ApiFactory? api;
    private IHost? dispatcher;

    public TestWebhookServer Webhooks { get; } = new();

    /// <summary>API client authenticated with the admin key (can call every endpoint).</summary>
    public HttpClient Api { get; private set; } = null!;

    public ServiceBusClient ServiceBus { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), serviceBus.StartAsync(), Webhooks.StartAsync());

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = postgres.GetConnectionString(),
            ["ConnectionStrings:ServiceBus"] = serviceBus.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            ["ApiKeys:Admin:0"] = AdminKey,
            ["ApiKeys:Publisher:0"] = PublisherKey,
            ["Outbox:PollingInterval"] = "00:00:00.100",
            ["Webhooks:AllowedHosts:0"] = "127.0.0.1",
            ["Webhooks:RequireHttps"] = "false",
            ["Webhooks:AllowPrivateNetworks"] = "true", // the test webhook server listens on 127.0.0.1
            ["Webhooks:Timeout"] = "00:00:05",
            ["Delivery:PollingInterval"] = "00:00:00.100",
            ["Delivery:RetryBaseDelay"] = "00:00:00.200",
            ["Delivery:RetryMaxDelay"] = "00:00:01",
            ["Delivery:MaxAttempts"] = MaxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Logging:LogLevel:Default"] = "Warning",
        };

        // Creating the client starts the API host, which applies the migrations.
        api = new ApiFactory(settings);
        Api = CreateApiClient(AdminKey);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddDispatcher(builder.Configuration);
        dispatcher = builder.Build();
        await dispatcher.StartAsync();

        ServiceBus = new ServiceBusClient(serviceBus.GetConnectionString());
    }

    /// <summary>A client for the in-memory API; <paramref name="apiKey"/> null sends no key.</summary>
    public HttpClient CreateApiClient(string? apiKey)
    {
        var client = api!.CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
    }

    public EventRelayDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<EventRelayDbContext>();
        EventRelayDbContext.Configure(options, postgres.GetConnectionString());
        return new EventRelayDbContext(options.Options);
    }

    public async ValueTask DisposeAsync()
    {
        if (dispatcher is not null)
        {
            await dispatcher.StopAsync();
            dispatcher.Dispose();
        }

        if (api is not null)
        {
            await api.DisposeAsync();
        }

        if (ServiceBus is not null)
        {
            await ServiceBus.DisposeAsync();
        }

        await Webhooks.DisposeAsync();
        await serviceBus.DisposeAsync();
        await postgres.DisposeAsync();
    }

    private sealed class ApiFactory(IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings));
        }
    }
}

[CollectionDefinition(Name)]
public sealed class EventRelayCollection : ICollectionFixture<EventRelayFixture>
{
    public const string Name = "EventRelay";
}
