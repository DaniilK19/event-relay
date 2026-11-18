using EventRelay.Api.Security;
using EventRelay.Core;
using EventRelay.Infrastructure.Persistence;
using EventRelay.Infrastructure.Webhooks;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace EventRelay.Api.Endpoints;

public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/subscriptions").WithTags("Subscriptions").RequireAuthorization(ApiKeyPolicies.Admin);

        group.MapPost("/", CreateAsync)
            .WithSummary("Create a subscription")
            .WithDescription("The webhook host must be on the allowlist. The signing secret is returned only in this response.");
        group.MapGet("/", ListAsync).WithSummary("List subscriptions");
        group.MapGet("/{id:guid}", GetAsync).WithSummary("Get a subscription");
        group.MapPut("/{id:guid}", UpdateAsync).WithSummary("Replace a subscription; pass 'secret' to rotate it");
        group.MapDelete("/{id:guid}", DeleteAsync).WithSummary("Delete a subscription and its deliveries");
        group.MapGet("/{id:guid}/deliveries", GetDeliveriesAsync).WithSummary("Most recent deliveries of a subscription");

        return app;
    }

    private static async Task<Results<Created<SubscriptionCreatedResponse>, ValidationProblem>> CreateAsync(
        CreateSubscriptionRequest request,
        EventRelayDbContext db,
        WebhookAllowlist allowlist,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request.Name, request.EventTypes, request.Filter, request.WebhookUrl, request.Secret, allowlist, out var url);
        if (!errors.IsValid)
        {
            return errors.ToProblem();
        }

        var secret = request.Secret ?? Subscription.GenerateSecret();
        var subscription = Subscription.Create(request.Name!, request.EventTypes!, request.Filter, url!, secret, time.GetUtcNow());

        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created(
            $"/subscriptions/{subscription.Id}",
            new SubscriptionCreatedResponse(SubscriptionResponse.From(subscription), secret));
    }

    private static async Task<Ok<SubscriptionResponse[]>> ListAsync(EventRelayDbContext db, CancellationToken cancellationToken)
    {
        var subscriptions = await db.Subscriptions.AsNoTracking()
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(subscriptions.Select(SubscriptionResponse.From).ToArray());
    }

    private static async Task<Results<Ok<SubscriptionResponse>, NotFound>> GetAsync(
        Guid id, EventRelayDbContext db, CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        return subscription is null ? TypedResults.NotFound() : TypedResults.Ok(SubscriptionResponse.From(subscription));
    }

    private static async Task<Results<Ok<SubscriptionResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid id,
        UpdateSubscriptionRequest request,
        EventRelayDbContext db,
        WebhookAllowlist allowlist,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request.Name, request.EventTypes, request.Filter, request.WebhookUrl, request.Secret, allowlist, out var url);
        if (!errors.IsValid)
        {
            return errors.ToProblem();
        }

        var subscription = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (subscription is null)
        {
            return TypedResults.NotFound();
        }

        var now = time.GetUtcNow();
        subscription.Update(request.Name!, request.EventTypes!, request.Filter, url!, request.IsActive ?? true, now);
        if (request.Secret is not null)
        {
            subscription.RotateSecret(request.Secret, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(SubscriptionResponse.From(subscription));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid id, EventRelayDbContext db, CancellationToken cancellationToken)
    {
        var deleted = await db.Subscriptions.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<DeliveryResponse[]>, NotFound>> GetDeliveriesAsync(
        Guid id, EventRelayDbContext db, CancellationToken cancellationToken, int limit = 50)
    {
        if (!await db.Subscriptions.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var deliveries = await db.Deliveries.AsNoTracking()
            .Where(d => d.SubscriptionId == id)
            .OrderByDescending(d => d.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(deliveries.Select(DeliveryResponse.From).ToArray());
    }

    private static ValidationErrors Validate(
        string? name,
        string[]? eventTypes,
        Dictionary<string, string>? filter,
        string? webhookUrl,
        string? secret,
        WebhookAllowlist allowlist,
        out Uri? url)
    {
        var errors = new ValidationErrors();
        url = null;

        if (string.IsNullOrWhiteSpace(name) || name.Length > Subscription.MaxNameLength)
        {
            errors.Add("name", $"Required, at most {Subscription.MaxNameLength} characters.");
        }

        if (eventTypes is null || eventTypes.Length is 0 or > Subscription.MaxEventTypes)
        {
            errors.Add("eventTypes", $"Between 1 and {Subscription.MaxEventTypes} patterns are required.");
        }
        else
        {
            foreach (var pattern in eventTypes.Where(p => !EventTypeRules.IsValidPattern(p)))
            {
                errors.Add("eventTypes", $"'{pattern}' is not valid. Use 'flight.delayed', 'flight.*' or '*'.");
            }
        }

        if (AttributeRules.Validate(filter) is { } filterProblem)
        {
            errors.Add("filter", filterProblem);
        }

        if (secret is not null && secret.Length is < Subscription.MinSecretLength or > Subscription.MaxSecretLength)
        {
            errors.Add("secret", $"Must be between {Subscription.MinSecretLength} and {Subscription.MaxSecretLength} characters.");
        }

        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var parsed))
        {
            errors.Add("webhookUrl", "Required, must be an absolute URL.");
        }
        else if (!allowlist.IsAllowed(parsed, out var reason))
        {
            errors.Add("webhookUrl", reason switch
            {
                WebhookAllowlist.HttpsRequired => "Must use https.",
                WebhookAllowlist.HostNotAllowed => $"Host '{parsed.Host}' is not on the webhook allowlist.",
                _ => "Must be an http(s) URL without credentials or fragment.",
            });
        }
        else
        {
            url = parsed;
        }

        return errors;
    }
}
