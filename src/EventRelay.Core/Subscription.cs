using System.Security.Cryptography;

namespace EventRelay.Core;

/// <summary>
/// A consumer's interest in events: which types, which attribute values,
/// and where to POST them. Each subscription has its own signing secret.
/// </summary>
public sealed class Subscription
{
    public const int MaxNameLength = 100;
    public const int MaxEventTypes = 20;
    public const int MinSecretLength = 16;
    public const int MaxSecretLength = 256;
    public const int MaxUrlLength = 2048;

    private Subscription()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>Type patterns: exact types, prefix wildcards (<c>flight.*</c>) or <c>*</c>.</summary>
    public string[] EventTypes { get; private set; } = [];

    /// <summary>Attribute values an event must have (exact, case-sensitive match on every entry).</summary>
    public IReadOnlyDictionary<string, string> Filter { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string WebhookUrl { get; private set; } = null!;

    /// <summary>HMAC key for the X-Signature header. Must be readable to sign, so it is not hashed.</summary>
    public string Secret { get; private set; } = null!;

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Subscription Create(
        string name,
        IEnumerable<string> eventTypes,
        IReadOnlyDictionary<string, string>? filter,
        Uri webhookUrl,
        string secret,
        DateTimeOffset now)
    {
        var subscription = new Subscription
        {
            Id = Guid.CreateVersion7(now),
            CreatedAt = now.ToUniversalTime(),
        };

        subscription.Update(name, eventTypes, filter, webhookUrl, isActive: true, now);
        subscription.RotateSecret(secret, now);
        return subscription;
    }

    public void Update(
        string name,
        IEnumerable<string> eventTypes,
        IReadOnlyDictionary<string, string>? filter,
        Uri webhookUrl,
        bool isActive,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > MaxNameLength)
        {
            throw new ArgumentException($"Name must be at most {MaxNameLength} characters.", nameof(name));
        }

        var types = eventTypes.Distinct(StringComparer.Ordinal).ToArray();
        if (types.Length is 0 or > MaxEventTypes)
        {
            throw new ArgumentException($"Between 1 and {MaxEventTypes} event types are required.", nameof(eventTypes));
        }

        if (types.FirstOrDefault(t => !EventTypeRules.IsValidPattern(t)) is { } invalid)
        {
            throw new ArgumentException($"'{invalid}' is not a valid event type pattern.", nameof(eventTypes));
        }

        if (AttributeRules.Validate(filter) is { } problem)
        {
            throw new ArgumentException(problem, nameof(filter));
        }

        if (!IsSupportedWebhookUrl(webhookUrl))
        {
            throw new ArgumentException("Webhook URL must be an absolute http(s) URL without credentials.", nameof(webhookUrl));
        }

        Name = name;
        EventTypes = types;
        Filter = filter is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(filter, StringComparer.Ordinal);
        WebhookUrl = webhookUrl.AbsoluteUri;
        IsActive = isActive;
        UpdatedAt = now.ToUniversalTime();
    }

    public void RotateSecret(string secret, DateTimeOffset now)
    {
        if (secret is null || secret.Length is < MinSecretLength or > MaxSecretLength)
        {
            throw new ArgumentException(
                $"Secret must be between {MinSecretLength} and {MaxSecretLength} characters.", nameof(secret));
        }

        Secret = secret;
        UpdatedAt = now.ToUniversalTime();
    }

    /// <summary>Generates a random 256-bit secret, e.g. <c>whsec_Q2x...</c>.</summary>
    public static string GenerateSecret() =>
        "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Structural URL rules that hold regardless of deployment. Which hosts are allowed
    /// is a separate, configurable policy (see the webhook allowlist in Infrastructure).
    /// </summary>
    public static bool IsSupportedWebhookUrl(Uri? url) =>
        url is { IsAbsoluteUri: true }
        && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(url.UserInfo)
        && string.IsNullOrEmpty(url.Fragment)
        && url.AbsoluteUri.Length <= MaxUrlLength;
}
