using System.Text.Json.Serialization;
using EventRelay.Api.Endpoints;
using EventRelay.Api.Security;
using EventRelay.Api.Workers;
using EventRelay.Infrastructure;
using EventRelay.Infrastructure.Persistence;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Events are small JSON documents (payload <= 64 KiB); reject anything far larger early.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 256 * 1024);

builder.Services
    .AddEventRelayPersistence()
    .AddEventRelayServiceBus(builder.Configuration)
    .AddEventRelayOutbox(builder.Configuration)
    .AddEventRelayWebhookAllowlist(builder.Configuration);

builder.Services.AddHostedService<OutboxPublisher>();

builder.Services.AddApiKeyAuthentication(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<ApiKeyOpenApiTransformer>());
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHealthChecks().AddDbContextCheck<EventRelayDbContext>("postgres");

var app = builder.Build();

// Convenient for docker compose and tests. In a real deployment, run migrations as a
// separate release step (e.g. an EF migration bundle) instead of from every replica.
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await DatabaseMigrator.MigrateAsync(
        app.Configuration.GetConnectionString(ServiceCollectionExtensions.PostgresConnectionName)
            ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured."),
        app.Services.GetRequiredService<ILoggerFactory>());
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

// Public: the API description and reference page contain no data (calls made from it still need a key).
app.MapOpenApi().AllowAnonymous();
app.MapScalarApiReference(options => options.WithTitle("EventRelay API")).AllowAnonymous();
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription().AllowAnonymous();

// Healthy means "can accept events" (database reachable). Service Bus is deliberately not
// part of it: thanks to the outbox, the API keeps accepting events while the broker is down.
// Anonymous for container probes; it only reports Healthy/Unhealthy.
app.MapHealthChecks("/health").AllowAnonymous();

app.MapEventEndpoints();        // publisher or admin key
app.MapSubscriptionEndpoints(); // admin key

await app.RunAsync();
