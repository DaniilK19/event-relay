using EventRelay.Infrastructure.Outbox;
using Microsoft.Extensions.Options;

namespace EventRelay.Api.Workers;

/// <summary>
/// Moves events from the outbox table to Service Bus. Runs inside the API process so that
/// accepting an event never depends on the broker being available: if Service Bus is down,
/// events simply wait in the outbox.
/// </summary>
public sealed partial class OutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    TimeProvider time,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                var published = await processor.PublishBatchAsync(settings.BatchSize, stoppingToken);

                // A full batch means more is probably waiting: continue without sleeping.
                delay = published == settings.BatchSize ? TimeSpan.Zero : settings.PollingInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogUnexpectedError(exception, settings.ErrorDelay);
                delay = settings.ErrorDelay;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox publisher failed; retrying in {Delay}")]
    private partial void LogUnexpectedError(Exception exception, TimeSpan delay);
}
