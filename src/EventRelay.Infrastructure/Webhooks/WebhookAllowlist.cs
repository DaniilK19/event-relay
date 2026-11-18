using System.Diagnostics.CodeAnalysis;
using EventRelay.Core;
using Microsoft.Extensions.Options;

namespace EventRelay.Infrastructure.Webhooks;

/// <summary>
/// Decides whether EventRelay may send requests to a URL. Checked when a subscription is
/// saved and again right before every delivery attempt, so tightening the configuration
/// takes effect for existing subscriptions too (see docs/adr/003-webhook-allowlist.md).
/// </summary>
public sealed class WebhookAllowlist(IOptionsMonitor<WebhookOptions> options)
{
    public const string UnsupportedUrl = "url_not_supported";
    public const string HttpsRequired = "https_required";
    public const string HostNotAllowed = "host_not_allowed";

    public bool IsAllowed(Uri url, [NotNullWhen(false)] out string? reason)
    {
        var current = options.CurrentValue;

        if (!Subscription.IsSupportedWebhookUrl(url))
        {
            reason = UnsupportedUrl;
            return false;
        }

        if (current.RequireHttps && url.Scheme != Uri.UriSchemeHttps)
        {
            reason = HttpsRequired;
            return false;
        }

        // IdnHost is the punycode form, so look-alike Unicode hosts cannot slip past an ASCII entry.
        var host = Normalize(url.IdnHost);
        if (!current.AllowedHosts.Any(entry => HostMatches(entry, host)))
        {
            reason = HostNotAllowed;
            return false;
        }

        reason = null;
        return true;
    }

    internal static bool HostMatches(string entry, string normalizedHost)
    {
        var pattern = Normalize(entry);
        if (pattern.Length == 0)
        {
            return false;
        }

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..]; // ".example.com"
            return normalizedHost.Length > suffix.Length
                   && normalizedHost.EndsWith(suffix, StringComparison.Ordinal);
        }

        return string.Equals(pattern, normalizedHost, StringComparison.Ordinal);
    }

    private static string Normalize(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();
}
