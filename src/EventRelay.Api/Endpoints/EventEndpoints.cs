using System.Text;
using System.Text.Json;
using EventRelay.Api.Security;
using EventRelay.Core;
using EventRelay.Infrastructure.Messaging;
using EventRelay.Infrastructure.Outbox;
using EventRelay.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace EventRelay.Api.Endpoints;

public static class EventEndpoints
{
    public const int MaxSourceLength = 200;
    public const int MaxDataBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/events").WithTags("Events").RequireAuthorization(ApiKeyPolicies.Publish);

        group.MapPost("/", PublishAsync)
            .WithSummary("Accept an event for delivery")
            .WithDescription(
                "Stores the event and an outbox message in one transaction and returns 202. " +
                "Re-sending the same id is idempotent and returns 200 with duplicate=true.");

        group.MapGet("/{id:guid}", GetAsync).WithSummary("Get an accepted event");

        group.MapGet("/{id:guid}/deliveries", GetDeliveriesAsync)
            .WithSummary("Delivery status of an event for every matching subscription");

        return app;
    }

    private static async Task<Results<Accepted<EventAcceptedResponse>, Ok<EventAcceptedResponse>, ValidationProblem, ProblemHttpResult>>
        PublishAsync(PublishEventRequest request, EventRelayDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (!errors.IsValid)
        {
            return errors.ToProblem();
        }

        var now = time.GetUtcNow();
        var evt = new Event(
            request.Id ?? Guid.CreateVersion7(now),
            request.Type!,
            request.Source!,
            request.Time ?? now,
            request.Attributes,
            request.Data is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } data ? data.GetRawText() : null,
            receivedAt: now);

        // Cheap check for the common retry case; the unique key below still covers races.
        var existing = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == evt.Id, cancellationToken);
        if (existing is not null)
        {
            return DuplicateOrConflict(existing, evt);
        }

        // The event and its outbox message are saved by a single SaveChanges call, i.e. in one
        // database transaction: either both rows exist or neither does. An accepted event can
        // therefore never be lost between the database and the broker.
        db.Events.Add(evt);
        db.OutboxMessages.Add(OutboxMessage.Create(evt.Id, evt.Type, EventEnvelope.FromEvent(evt).ToJson(), now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            db.ChangeTracker.Clear();
            existing = await db.Events.AsNoTracking().FirstAsync(e => e.Id == evt.Id, cancellationToken);
            return DuplicateOrConflict(existing, evt);
        }

        return TypedResults.Accepted($"/events/{evt.Id}", new EventAcceptedResponse(evt.Id, Duplicate: false));
    }

    private static Results<Accepted<EventAcceptedResponse>, Ok<EventAcceptedResponse>, ValidationProblem, ProblemHttpResult>
        DuplicateOrConflict(Event existing, Event incoming) =>
        existing.IsSameEventAs(incoming)
            ? TypedResults.Ok(new EventAcceptedResponse(existing.Id, Duplicate: true))
            : TypedResults.Problem(
                title: "Event id already used",
                detail: $"Event {existing.Id} already exists with a different type or source.",
                statusCode: StatusCodes.Status409Conflict);

    private static async Task<Results<Ok<EventResponse>, NotFound>> GetAsync(
        Guid id, EventRelayDbContext db, CancellationToken cancellationToken)
    {
        var evt = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (evt is null)
        {
            return TypedResults.NotFound();
        }

        var envelope = EventEnvelope.FromEvent(evt);
        return TypedResults.Ok(new EventResponse(
            evt.Id, evt.Type, evt.Source, evt.Time, evt.Attributes, envelope.Data, evt.ReceivedAt));
    }

    private static async Task<Results<Ok<DeliveryResponse[]>, NotFound>> GetDeliveriesAsync(
        Guid id, EventRelayDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Events.AnyAsync(e => e.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var deliveries = await db.Deliveries.AsNoTracking()
            .Where(d => d.EventId == id)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(deliveries.Select(DeliveryResponse.From).ToArray());
    }

    private static ValidationErrors Validate(PublishEventRequest request)
    {
        var errors = new ValidationErrors();

        if (request.Id == Guid.Empty)
        {
            errors.Add("id", "Must not be the empty GUID.");
        }

        if (!EventTypeRules.IsValidType(request.Type))
        {
            errors.Add("type", "Required. Lowercase segments separated by '.', '_' or '-', e.g. 'flight.delayed'.");
        }

        if (string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > MaxSourceLength)
        {
            errors.Add("source", $"Required, at most {MaxSourceLength} characters.");
        }

        if (AttributeRules.Validate(request.Attributes) is { } attributesProblem)
        {
            errors.Add("attributes", attributesProblem);
        }

        if (request.Data is { ValueKind: not JsonValueKind.Null } data
            && Encoding.UTF8.GetByteCount(data.GetRawText()) > MaxDataBytes)
        {
            errors.Add("data", $"Must be at most {MaxDataBytes / 1024} KiB of JSON.");
        }

        return errors;
    }
}
