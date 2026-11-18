using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebhookReceiver;

/// <summary>
/// What every EventRelay subscriber should do before trusting a request:
/// 1. check that X-Timestamp is recent (replay protection);
/// 2. recompute HMAC-SHA256(secret, "{X-Timestamp}.{raw body}") and compare it with
///    X-Signature in constant time.
/// Written independently of EventRelay's own code on purpose: this is the consumer's side.
/// </summary>
public static class SignatureVerifier
{
    private const string Prefix = "sha256=";

    public static bool Verify(
        string secret,
        string? timestampHeader,
        string? signatureHeader,
        ReadOnlySpan<byte> body,
        DateTimeOffset now,
        TimeSpan tolerance,
        [NotNullWhen(false)] out string? failure)
    {
        if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            failure = "missing or malformed X-Timestamp";
            return false;
        }

        if ((now - DateTimeOffset.FromUnixTimeSeconds(timestamp)).Duration() > tolerance)
        {
            failure = "X-Timestamp is outside the allowed window (possible replay)";
            return false;
        }

        if (signatureHeader is null
            || !signatureHeader.StartsWith(Prefix, StringComparison.Ordinal)
            || !TryDecodeHex(signatureHeader.AsSpan(Prefix.Length), out var provided))
        {
            failure = "missing or malformed X-Signature";
            return false;
        }

        var prefix = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var signedContent = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signedContent, 0);
        body.CopyTo(signedContent.AsSpan(prefix.Length));

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signedContent);
        if (!CryptographicOperations.FixedTimeEquals(expected, provided))
        {
            failure = "signature mismatch";
            return false;
        }

        failure = null;
        return true;
    }

    private static bool TryDecodeHex(ReadOnlySpan<char> hex, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
