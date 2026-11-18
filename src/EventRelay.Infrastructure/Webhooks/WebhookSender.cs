using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace EventRelay.Infrastructure.Webhooks;

public sealed record WebhookRequest(
    Uri Url,
    string Secret,
    Guid DeliveryId,
    Guid EventId,
    string EventType,
    int Attempt,
    byte[] Body);

/// <param name="Error">Short relay-generated code (<c>http_503</c>, <c>timeout</c>...), never response content.</param>
/// <param name="IsPermanent">Retrying cannot help, e.g. the host is not allowed.</param>
public sealed record WebhookResult(bool Succeeded, int? StatusCode, string? Error, bool IsPermanent = false)
{
    public static WebhookResult Success(int statusCode) => new(true, statusCode, null);

    public static WebhookResult Failure(int? statusCode, string error) => new(false, statusCode, error);

    public static WebhookResult Rejected(string reason) => new(false, null, reason, IsPermanent: true);
}

/// <summary>
/// Sends one signed webhook request. The HttpClient it receives is configured in
/// <see cref="ServiceCollectionExtensions.AddEventRelayWebhookSender"/>: connect-time IP check, no redirects,
/// no cookies, strict timeouts.
/// </summary>
public sealed partial class WebhookSender(
    HttpClient http,
    WebhookAllowlist allowlist,
    TimeProvider time,
    ILogger<WebhookSender> logger)
{
    public const string EventIdHeader = "X-Event-Id";
    public const string EventTypeHeader = "X-Event-Type";
    public const string DeliveryIdHeader = "X-Delivery-Id";
    public const string AttemptHeader = "X-Delivery-Attempt";
    public const string TimestampHeader = "X-Timestamp";
    public const string SignatureHeader = "X-Signature";

    public async Task<WebhookResult> SendAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        // Defense in depth: the URL was validated when the subscription was saved,
        // but the allowlist may have been tightened since.
        if (!allowlist.IsAllowed(request.Url, out var reason))
        {
            return WebhookResult.Rejected(reason);
        }

        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();

        using var message = new HttpRequestMessage(HttpMethod.Post, request.Url);
        message.Content = new ByteArrayContent(request.Body);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        message.Headers.Add(EventIdHeader, request.EventId.ToString());
        message.Headers.Add(EventTypeHeader, request.EventType);
        message.Headers.Add(DeliveryIdHeader, request.DeliveryId.ToString());
        message.Headers.Add(AttemptHeader, request.Attempt.ToString(CultureInfo.InvariantCulture));
        message.Headers.Add(TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        message.Headers.Add(SignatureHeader, WebhookSignature.Compute(request.Secret, timestamp, request.Body));

        try
        {
            // Only the status line and headers are read. The response body is never buffered,
            // logged or stored, so a webhook cannot be used to read data back through EventRelay.
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var statusCode = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? WebhookResult.Success(statusCode)
                : WebhookResult.Failure(statusCode, $"http_{statusCode}");
        }
        catch (HttpRequestException exception) when (exception.InnerException is WebhookAddressNotAllowedException blocked)
        {
            // Nothing was sent. The address stays in the logs for operators; the API only shows the code.
            LogAddressNotAllowed(request.DeliveryId, blocked.Host, blocked.Address, blocked.Scope);
            return WebhookResult.Rejected("address_not_allowed");
        }
        catch (HttpRequestException exception)
        {
            LogTransportError(exception, request.DeliveryId);
            return WebhookResult.Failure(null, "connection_failed");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebhookResult.Failure(null, "timeout");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Delivery {DeliveryId} rejected: webhook host {Host} resolves to {Address} ({Scope})")]
    private partial void LogAddressNotAllowed(Guid deliveryId, string host, System.Net.IPAddress address, AddressScope scope);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Webhook request for delivery {DeliveryId} failed at transport level")]
    private partial void LogTransportError(Exception exception, Guid deliveryId);
}
