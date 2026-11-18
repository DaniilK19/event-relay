namespace EventRelay.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>How often to look for unpublished messages when the outbox was empty.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Pause after an unexpected error (e.g. database unavailable) before trying again.</summary>
    public TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 100;
}
