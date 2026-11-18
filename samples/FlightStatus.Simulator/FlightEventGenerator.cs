using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace FlightStatus.Simulator;

/// <summary>Posts a random flight.delayed / flight.gate_changed / flight.cancelled event every few seconds.</summary>
public sealed partial class FlightEventGenerator(
    IHttpClientFactory httpClientFactory,
    IOptions<SimulatorOptions> options,
    TimeProvider time,
    ILogger<FlightEventGenerator> logger) : BackgroundService
{
    public const string HttpClientName = "eventrelay";

    private readonly Random random = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        object? previous = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var resend = previous is not null && random.NextDouble() < settings.DuplicateRate;
            var (evt, summary) = resend ? (previous!, "duplicate of the previous event") : CreateEvent(settings.Source);

            try
            {
                var http = httpClientFactory.CreateClient(HttpClientName);
                using var response = await http.PostAsJsonAsync("/events", evt, stoppingToken);
                if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK)
                {
                    LogPublished((int)response.StatusCode, summary);
                    previous = evt;
                }
                else
                {
                    LogRejected((int)response.StatusCode, await response.Content.ReadAsStringAsync(stoppingToken));
                }
            }
            catch (HttpRequestException exception)
            {
                // Typical right after `docker compose up`, while the API is still starting.
                LogApiUnavailable(settings.EventRelayUrl, exception.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await Task.Delay(settings.Interval, time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private (object Event, string Summary) CreateEvent(string source)
    {
        var flight = FlightSchedule.Flights[random.Next(FlightSchedule.Flights.Count)];
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var scheduled = new DateTimeOffset(today.ToDateTime(flight.Departure), TimeSpan.Zero);

        var attributes = new Dictionary<string, string>
        {
            ["airline"] = flight.Airline.Code,
            ["flightNumber"] = flight.FlightNumber,
            ["origin"] = flight.Origin,
            ["destination"] = flight.Destination,
        };

        var roll = random.NextDouble();
        (string type, object data, string summary) = roll switch
        {
            < 0.5 => Delayed(flight, scheduled),
            < 0.85 => GateChanged(flight),
            _ => Cancelled(flight),
        };

        var evt = new
        {
            id = Guid.CreateVersion7(now),
            type,
            source,
            time = now,
            attributes,
            data,
        };

        return (evt, $"{type} {flight.FlightNumber} {flight.Origin}→{flight.Destination}: {summary}");
    }

    private (string, object, string) Delayed(Flight flight, DateTimeOffset scheduled)
    {
        var minutes = random.Next(1, 25) * 5;
        var reason = FlightSchedule.DelayReasons[random.Next(FlightSchedule.DelayReasons.Count)];
        return ("flight.delayed",
            new
            {
                flightNumber = flight.FlightNumber,
                airline = flight.Airline.Name,
                scheduledDeparture = scheduled,
                estimatedDeparture = scheduled.AddMinutes(minutes),
                delayMinutes = minutes,
                reason,
            },
            $"+{minutes} min ({reason})");
    }

    private (string, object, string) GateChanged(Flight flight)
    {
        var newGate = $"{(char)('A' + random.Next(4))}{random.Next(1, 12)}";
        return ("flight.gate_changed",
            new
            {
                flightNumber = flight.FlightNumber,
                airline = flight.Airline.Name,
                terminal = flight.Terminal,
                previousGate = flight.Gate,
                newGate,
            },
            $"gate {flight.Gate} → {newGate}");
    }

    private (string, object, string) Cancelled(Flight flight)
    {
        var reason = FlightSchedule.CancellationReasons[random.Next(FlightSchedule.CancellationReasons.Count)];
        return ("flight.cancelled",
            new
            {
                flightNumber = flight.FlightNumber,
                airline = flight.Airline.Name,
                reason,
            },
            reason);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "HTTP {StatusCode}: {Summary}")]
    private partial void LogPublished(int statusCode, string summary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EventRelay rejected the event with HTTP {StatusCode}: {Body}")]
    private partial void LogRejected(int statusCode, string body);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EventRelay at {Url} is not reachable yet ({Reason}); will retry")]
    private partial void LogApiUnavailable(Uri url, string reason);
}
