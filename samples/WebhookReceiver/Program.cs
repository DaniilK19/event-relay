using System.Text.Json;
using Microsoft.Extensions.Options;
using WebhookReceiver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ReceiverOptions>()
    .Bind(builder.Configuration.GetSection(ReceiverOptions.SectionName))
    .Validate(o => o.Secret.Length >= 16, "Receiver:Secret must be at least 16 characters (set Receiver__Secret).")
    .Validate(o => !o.RegisterSubscriptions || o.ApiKey.Length > 0, "Receiver:ApiKey (an EventRelay admin key) is required to register subscriptions.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ReceivedEvents>();
builder.Services.AddHttpClient(SubscriptionRegistrar.HttpClientName, (provider, client) =>
{
    var settings = provider.GetRequiredService<IOptions<ReceiverOptions>>().Value;
    client.BaseAddress = settings.EventRelayUrl;
    client.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);
});
builder.Services.AddHostedService<SubscriptionRegistrar>();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WebhookReceiver");

app.MapPost("/webhooks/{endpoint}", async (
    string endpoint,
    HttpRequest request,
    IOptions<ReceiverOptions> options,
    ReceivedEvents received,
    TimeProvider time,
    CancellationToken cancellationToken) =>
{
    var settings = options.Value;

    // The signature covers the exact bytes, so read the raw body and verify before parsing.
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, cancellationToken);
    var body = buffer.ToArray();

    // Every endpoint has its own secret, so a request signed for one endpoint fails on all others.
    if (!SignatureVerifier.Verify(
            settings.SigningSecretFor(endpoint),
            request.Headers["X-Timestamp"],
            request.Headers["X-Signature"],
            body,
            time.GetUtcNow(),
            settings.SignatureTolerance,
            out var failure))
    {
        log.LogWarning("[{Endpoint}] rejected request: {Failure}", endpoint, failure);
        return Results.Unauthorized();
    }

    // Only X-Timestamp and the body are signed. X-Event-Id and the other headers can be changed in a
    // replayed request, so the event id used for de-duplication comes from the verified body.
    if (!TryReadEvent(body, out var payload, out var eventId))
    {
        log.LogWarning("[{Endpoint}] rejected request: body is not an event with an id", endpoint);
        return Results.BadRequest();
    }

    var attempt = request.Headers["X-Delivery-Attempt"].ToString(); // informational, for the log only

    if (endpoint == "flaky" && Random.Shared.NextDouble() < settings.FlakyFailureRate)
    {
        log.LogWarning("[{Endpoint}] simulating an outage for event {EventId} (attempt {Attempt}) → 503", endpoint, eventId, attempt);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (!received.TryAdd(new ReceivedEvent(endpoint, eventId, time.GetUtcNow(), payload)))
    {
        log.LogInformation("[{Endpoint}] duplicate delivery of event {EventId} ignored", endpoint, eventId);
        return Results.Ok();
    }

    var type = payload.GetProperty("type").GetString();
    var flight = payload.TryGetProperty("attributes", out var attributes)
                 && attributes.TryGetProperty("flightNumber", out var number)
        ? number.GetString()
        : "-";
    log.LogInformation("[{Endpoint}] {Type} {Flight} (event {EventId}, attempt {Attempt}) ✓ signature valid",
        endpoint, type, flight, eventId, attempt);

    return Results.Ok();
});

app.MapGet("/received", (ReceivedEvents received, int? count) => received.Latest(Math.Clamp(count ?? 20, 1, 200)));
app.MapGet("/health", () => Results.Ok("healthy"));

await app.RunAsync();

static bool TryReadEvent(byte[] body, out JsonElement payload, out Guid eventId)
{
    payload = default;
    eventId = Guid.Empty;

    try
    {
        using var document = JsonDocument.Parse(body);
        payload = document.RootElement.Clone();
    }
    catch (JsonException)
    {
        return false;
    }

    return payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty("id", out var id)
           && id.ValueKind == JsonValueKind.String
           && id.TryGetGuid(out eventId);
}
