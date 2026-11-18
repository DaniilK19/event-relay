using System.Net;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using EventRelay.Infrastructure.Messaging;
using EventRelay.Infrastructure.Outbox;
using EventRelay.Infrastructure.Persistence;
using EventRelay.Infrastructure.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EventRelay.Infrastructure;

public static class ServiceCollectionExtensions
{
    public const string PostgresConnectionName = "Postgres";
    public const string ServiceBusConnectionName = "ServiceBus";

    /// <summary>EF Core over PostgreSQL. Reads <c>ConnectionStrings:Postgres</c> when the context is first needed.</summary>
    public static IServiceCollection AddEventRelayPersistence(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<EventRelayDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString(PostgresConnectionName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"Connection string '{PostgresConnectionName}' is not configured.");
            }

            EventRelayDbContext.Configure(options, connectionString);
        });

        return services;
    }

    /// <summary>A shared <see cref="ServiceBusClient"/> plus the publisher used by the outbox.</summary>
    public static IServiceCollection AddEventRelayServiceBus(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServiceBusOptions>()
            .Bind(configuration.GetSection(ServiceBusOptions.SectionName))
            .PostConfigure(options =>
                options.ConnectionString ??= configuration.GetConnectionString(ServiceBusConnectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString)
                           || !string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace),
                $"Configure either ConnectionStrings:{ServiceBusConnectionName} or ServiceBus:FullyQualifiedNamespace.")
            .ValidateOnStart();

        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ServiceBusOptions>>().Value;
            var clientOptions = new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    Mode = ServiceBusRetryMode.Exponential,
                    MaxRetries = 3,
                    TryTimeout = TimeSpan.FromSeconds(15),
                },
            };

            return string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace)
                ? new ServiceBusClient(options.ConnectionString, clientOptions)
                : new ServiceBusClient(options.FullyQualifiedNamespace, new DefaultAzureCredential(), clientOptions);
        });

        services.TryAddSingleton<IEventPublisher, ServiceBusEventPublisher>();
        return services;
    }

    public static IServiceCollection AddEventRelayOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .Validate(options => options.BatchSize is > 0 and <= 1000, "Outbox:BatchSize must be between 1 and 1000.")
            .ValidateOnStart();

        services.AddScoped<OutboxProcessor>();
        return services;
    }

    /// <summary>Only the allowlist: enough for the API to validate subscription URLs.</summary>
    public static IServiceCollection AddEventRelayWebhookAllowlist(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WebhookOptions>()
            .Bind(configuration.GetSection(WebhookOptions.SectionName))
            .Validate(options => options.Timeout > TimeSpan.Zero, "Webhooks:Timeout must be positive.")
            .ValidateOnStart();

        services.TryAddSingleton<WebhookAllowlist>();
        return services;
    }

    /// <summary>
    /// Allowlist plus a hardened HttpClient for outgoing webhooks: every connection goes through
    /// <see cref="WebhookConnectionGuard"/> (no private or link-local addresses), no proxy (it would
    /// bypass that check), redirects are not followed (a redirect could point anywhere), cookies
    /// are not kept between subscribers, and connect/overall timeouts are bounded.
    /// </summary>
    public static IServiceCollection AddEventRelayWebhookSender(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddEventRelayWebhookAllowlist(configuration);
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient<WebhookSender>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<WebhookOptions>>().Value;
                client.Timeout = options.Timeout;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("EventRelay-Webhooks/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var options = provider.GetRequiredService<IOptions<WebhookOptions>>().Value;
                return new SocketsHttpHandler
                {
                    ConnectCallback = new WebhookConnectionGuard(options.AllowPrivateNetworks).ConnectAsync,
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.None,
                    ConnectTimeout = options.ConnectTimeout,
                    MaxConnectionsPerServer = 20,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                };
            });

        return services;
    }
}
