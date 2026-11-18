using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace WebhookReceiver;

/// <summary>
/// On startup, makes sure EventRelay has the demo subscriptions pointing at this receiver
/// (create, or update in place when they already exist). Retries until the API is up.
/// </summary>
public sealed partial class SubscriptionRegistrar(
    IHttpClientFactory httpClientFactory,
    IOptions<ReceiverOptions> options,
    TimeProvider time,
    ILogger<SubscriptionRegistrar> logger) : BackgroundService
{
    public const string HttpClientName = "eventrelay";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private sealed record ExistingSubscription(Guid Id, string Name);

    /// <param name="Endpoint">The <c>{endpoint}</c> part of <c>/webhooks/{endpoint}</c>; also selects the signing secret.</param>
    private sealed record DesiredSubscription(string Name, string[] EventTypes, Dictionary<string, string> Filter, string Endpoint);

    private static readonly DesiredSubscription[] Desired =
    [
        new("receiver: all flight events", ["flight.*"], [], "all-flights"),
        new("receiver: Contoso Air cancellations", ["flight.cancelled"], new() { ["airline"] = "CT" }, "contoso-cancellations"),
        new("receiver: delays (flaky endpoint)", ["flight.delayed"], [], "flaky"),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RegisterSubscriptions)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RegisterAsync(stoppingToken);
                return;
            }
            catch (HttpRequestException exception)
            {
                LogApiUnavailable(options.Value.EventRelayUrl, exception.Message);
            }

            await Task.Delay(RetryDelay, time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var http = httpClientFactory.CreateClient(HttpClientName);
        var existing = await http.GetFromJsonAsync<ExistingSubscription[]>("/subscriptions", cancellationToken) ?? [];

        foreach (var desired in Desired)
        {
            var url = new Uri(settings.PublicBaseUrl, $"webhooks/{desired.Endpoint}");
            var body = new
            {
                name = desired.Name,
                eventTypes = desired.EventTypes,
                filter = desired.Filter,
                webhookUrl = url,
                isActive = true,
                secret = settings.SigningSecretFor(desired.Endpoint),
            };

            var current = existing.FirstOrDefault(s => s.Name == desired.Name);
            using var response = current is null
                ? await http.PostAsJsonAsync("/subscriptions", body, cancellationToken)
                : await http.PutAsJsonAsync($"/subscriptions/{current.Id}", body, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                LogRegistered(desired.Name, url);
            }
            else
            {
                LogRegistrationRejected(desired.Name, (int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription '{Name}' → {Url}")]
    private partial void LogRegistered(string name, Uri url);

    [LoggerMessage(Level = LogLevel.Error, Message = "EventRelay rejected subscription '{Name}' with HTTP {StatusCode}: {Problem}")]
    private partial void LogRegistrationRejected(string name, int statusCode, string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for EventRelay at {Url} ({Reason})")]
    private partial void LogApiUnavailable(Uri url, string reason);
}
