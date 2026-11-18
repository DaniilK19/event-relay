using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EventRelay.Infrastructure.Webhooks;

/// <summary>
/// <c>X-Signature: sha256=hex(HMAC-SHA256(secret, "{X-Timestamp}.{raw body}"))</c>.
/// Signing the timestamp together with the body lets receivers reject replays of old requests.
/// </summary>
public static class WebhookSignature
{
    public const string Prefix = "sha256=";

    public static string Compute(string secret, long unixTimestamp, ReadOnlySpan<byte> body)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(Encoding.UTF8.GetBytes(unixTimestamp.ToString(CultureInfo.InvariantCulture) + "."));
        hmac.AppendData(body);
        return Prefix + Convert.ToHexStringLower(hmac.GetHashAndReset());
    }
}
