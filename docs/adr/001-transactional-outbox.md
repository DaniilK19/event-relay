# ADR 001: Transactional outbox for publishing events

- **Status:** Accepted
- **Date:** 2026-11-18

## Context

When `POST /events` succeeds, the event must be stored *and* handed to the message broker so the
dispatcher can process it. Doing both directly is a dual write across two systems without a shared
transaction:

- write to the database, then publish: if the process dies or the broker is unavailable in between,
  the event is stored but never delivered;
- publish, then write: if the database write fails, a message refers to an event that does not exist,
  and the client got an error for an event that will still be delivered.

We also want the API to keep accepting events during a broker outage.

## Decision

Use a transactional outbox. The API inserts the event and an `outbox_messages` row in the **same
database transaction** (one `SaveChanges` call) and returns `202 Accepted`. A background
`OutboxPublisher` in the API process polls unpublished rows with
`SELECT … ORDER BY id LIMIT n FOR UPDATE SKIP LOCKED`, sends them to the `events` topic using the
event id as Service Bus `MessageId`, marks them published and commits.

## Consequences

- An accepted event is durable and will reach the broker eventually; broker downtime only delays it.
- The API's health depends on the database only.
- Publishing is **at-least-once**: a crash after sending but before the commit sends the batch again.
  Consumers must be idempotent (see [ADR 002](002-at-least-once-delivery.md)).
- Polling adds latency (default 500 ms when idle; a full batch is followed immediately by the next)
  and a small constant load on the database.
- `SKIP LOCKED` lets several API replicas publish concurrently without blocking each other.
- Published rows stay in the table; a retention job would delete old ones.
- Ordering across the topic is not guaranteed (concurrent publishers, retries).

## Alternatives considered

- **Publish directly from the request.** Simplest, but loses events or creates phantom messages on
  partial failure, and couples API availability to the broker.
- **Change data capture** (PostgreSQL logical replication / Debezium). No polling and lower latency,
  but a lot of extra infrastructure for a project of this size.
- **Distributed transaction.** Not supported by Azure Service Bus with PostgreSQL, and undesirable anyway.
- **Outbox library** (MassTransit, Wolverine, NServiceBus). Production-ready, but hides the mechanism
  this project exists to demonstrate.
