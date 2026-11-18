using EventRelay.Infrastructure;

namespace EventRelay.Dispatcher;

public static class DispatcherServiceCollectionExtensions
{
    /// <summary>Everything the dispatcher process runs. Public so integration tests can host it in-process.</summary>
    public static IServiceCollection AddDispatcher(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddEventRelayPersistence()
            .AddEventRelayServiceBus(configuration)
            .AddEventRelayWebhookSender(configuration);

        services.AddOptions<DeliveryOptions>()
            .Bind(configuration.GetSection(DeliveryOptions.SectionName))
            .Validate(o => o.BatchSize > 0 && o.MaxParallelism > 0, "Delivery:BatchSize and Delivery:MaxParallelism must be positive.")
            .Validate(o => o.Lease > TimeSpan.Zero && o.PollingInterval > TimeSpan.Zero, "Delivery:Lease and Delivery:PollingInterval must be positive.")
            .Validate(o => o.MaxAttempts > 0 && o.RetryBaseDelay > TimeSpan.Zero && o.RetryMaxDelay >= o.RetryBaseDelay,
                "Delivery retry settings are inconsistent.")
            .ValidateOnStart();

        services.AddScoped<EventFanOut>();
        services.AddHostedService<EventConsumer>();
        services.AddHostedService<DeliveryWorker>();
        return services;
    }
}
