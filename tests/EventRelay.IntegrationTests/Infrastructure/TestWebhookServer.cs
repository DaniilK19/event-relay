using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace EventRelay.IntegrationTests.Infrastructure;

public sealed record ReceivedWebhook(string Hook, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";
}

/// <summary>
/// A real HTTP endpoint on 127.0.0.1 (random port) that records every webhook request.
/// Each hook can be told which status code to return for the n-th request.
/// </summary>
public sealed class TestWebhookServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ReceivedWebhook>> received = new();
    private readonly ConcurrentDictionary<string, Func<int, int>> responses = new();
    private WebApplication? app;

    public Uri BaseAddress { get; private set; } = null!;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        app = builder.Build();
        app.MapPost("/hooks/{hook}", async (string hook, HttpRequest request) =>
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body);

            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var queue = received.GetOrAdd(hook, _ => new ConcurrentQueue<ReceivedWebhook>());
            queue.Enqueue(new ReceivedWebhook(hook, headers, body.ToArray()));

            var status = responses.TryGetValue(hook, out var respond) ? respond(queue.Count) : StatusCodes.Status200OK;
            return Results.StatusCode(status);
        });

        await app.StartAsync();
        BaseAddress = new Uri(app.Urls.First());
    }

    /// <summary>A fresh hook name, so tests never see each other's requests.</summary>
    public string NewHook() => Guid.NewGuid().ToString("N");

    public Uri UrlFor(string hook) => new(BaseAddress, $"/hooks/{hook}");

    /// <param name="statusForRequest">Maps the 1-based request number to the status code to return.</param>
    public void Respond(string hook, Func<int, int> statusForRequest) => responses[hook] = statusForRequest;

    public IReadOnlyList<ReceivedWebhook> Received(string hook) =>
        received.TryGetValue(hook, out var queue) ? queue.ToArray() : [];

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }
}
