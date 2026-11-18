namespace EventRelay.Infrastructure.Webhooks;

public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>
    /// Hosts webhooks may be sent to: exact names (<c>hooks.example.com</c>) or a
    /// subdomain wildcard (<c>*.example.com</c>, which does not match the apex).
    /// Empty means nothing is allowed.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>Reject plain-http webhook URLs. Disabled only for the local docker-compose demo.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Let webhooks connect to loopback and private-network addresses (10/8, 172.16/12,
    /// 192.168/16, fc00::/7...). Off by default; enabled only for the local demo and tests,
    /// where the receiver runs on localhost or a Docker network. Link-local addresses such as
    /// the cloud metadata endpoint 169.254.169.254 stay blocked either way.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>Upper bound for one delivery attempt, including connect and response headers.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
