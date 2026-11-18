# ADR 002: At-least-once delivery with idempotent processing

- **Status:** Accepted
- **Date:** 2026-11-18

## Context

Every hop in the pipeline can fail after doing its work but before recording it: the outbox
publisher after sending, Service Bus after delivering a message whose lock then expires, the
delivery worker after the subscriber answered but before the outcome was saved. "Exactly-once" across
an HTTP call to a third party is not achievable: if the response is lost, the sender cannot know
whether the subscriber processed the request.

## Decision

Guarantee **at-least-once** delivery end to end and make every step idempotent:

1. **Ingestion.** Producers may supply the event `id`. Re-posting the same id returns `200` with
   `duplicate: true`; reusing an id for a different type or source returns `409`.
2. **Fan-out (idempotent consumer).** The dispatcher inserts the broker `MessageId` into
   `processed_messages` with `ON CONFLICT DO NOTHING` in the same transaction that creates deliveries.
   If the row already exists, the message is acknowledged with no side effects. A unique key on
   `deliveries (event_id, subscription_id)` guarantees one delivery per subscription even if the same
   event arrives under a different message id.
3. **Webhooks.** Deliveries are retried with exponential backoff and jitter until a `2xx` response or
   until `MaxAttempts` is reached, after which the delivery is `Failed` and visible through the API.
   Every request carries the event `id` in its signed body (and, for convenience, in `X-Event-Id`).
   Subscribers de-duplicate on the body value: headers are not signed and can be altered in a replay.
4. **Poison messages.** Messages that can never succeed (malformed body, unknown event) are
   dead-lettered immediately. Unexpected errors abandon the message after a growing pause;
   Service Bus dead-letters it after `MaxDeliveryCount` (10) deliveries.

## Consequences

- No accepted event is silently dropped; every failure ends in a `Failed` delivery or a dead-lettered
  message that can be inspected and replayed.
- Subscribers can receive the same event more than once and must de-duplicate by the event `id`
  from the signed body (the sample receiver shows how).
- `processed_messages` grows with traffic and needs a retention policy longer than the broker's
  redelivery horizon.
- There is no ordering guarantee between events.
- Duplicates are rare in practice: they need a crash or timeout at exactly the wrong moment, or a
  webhook slower than the delivery lease.

## Alternatives considered

- **At-most-once** (acknowledge before processing). Simpler, but loses events on any crash, which
  defeats the purpose of the service.
- **Broker duplicate detection only.** Azure Service Bus can drop messages with a repeated
  `MessageId`, but only within a time window and only for publishing; it is enabled in the Terraform
  configuration as an extra layer, not relied upon.
- **Ordered delivery with sessions.** Possible per key (e.g. flight number), but introduces
  head-of-line blocking when one subscriber is slow; out of scope.
