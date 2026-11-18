namespace FlightStatus.Simulator;

public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    /// <summary>Base URL of the EventRelay API.</summary>
    public Uri EventRelayUrl { get; set; } = new("http://localhost:5080");

    /// <summary>EventRelay publisher API key (sent as X-Api-Key).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Pause between generated events.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Probability of re-sending the previous event with the same id, to show that
    /// POST /events is idempotent (EventRelay answers 200 with duplicate=true).
    /// </summary>
    public double DuplicateRate { get; set; } = 0.1;

    public string Source { get; set; } = "flight-status-simulator";
}
