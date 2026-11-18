using System.Security.Cryptography;
using System.Text;

namespace WebhookReceiver;

public sealed class ReceiverOptions
{
    public const string SectionName = "Receiver";

    /// <summary>Base URL of the EventRelay API, used to register this receiver's subscriptions.</summary>
    public Uri EventRelayUrl { get; set; } = new("http://localhost:5080");

    /// <summary>
    /// EventRelay admin API key, needed to register the subscriptions. A real subscriber would not
    /// hold one: an operator would create its subscriptions. Here it keeps the demo self-contained.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>How EventRelay reaches this receiver (inside docker compose: http://webhook-receiver:8080).</summary>
    public Uri PublicBaseUrl { get; set; } = new("http://localhost:5081");

    /// <summary>
    /// Master secret. Never used for signing as is: every endpoint gets its own secret derived from
    /// it (<see cref="SigningSecretFor"/>), which is registered with EventRelay and verifies X-Signature.
    /// </summary>
    public string Secret { get; set; } = "";

    /// <summary>Requests whose X-Timestamp is further away than this are rejected as possible replays.</summary>
    public TimeSpan SignatureTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Share of requests the <c>/webhooks/flaky</c> endpoint answers with 503, to show retries.</summary>
    public double FlakyFailureRate { get; set; } = 0.3;

    public bool RegisterSubscriptions { get; set; } = true;

    /// <summary>
    /// One signing secret per subscription, as EventRelay intends: a request captured on one endpoint
    /// does not verify on another. Derived from <see cref="Secret"/> so one value in .env is enough.
    /// </summary>
    public string SigningSecretFor(string endpoint) =>
        "whsec_" + Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret),
            Encoding.UTF8.GetBytes("webhook-receiver/" + endpoint)));
}
