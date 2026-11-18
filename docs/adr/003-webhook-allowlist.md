# ADR 003: Allowlist of webhook hosts

- **Status:** Accepted
- **Date:** 2026-11-18

## Context

A webhook URL is chosen by whoever creates a subscription, and EventRelay then makes HTTP requests to
it from inside its own network. Without controls this is a textbook server-side request forgery
(SSRF) vector: a subscription pointing at `http://169.254.169.254/` (cloud metadata),
`http://localhost:5432/` or an internal admin service could be used to reach systems that are not
exposed publicly, to scan the network through error messages and timings, or to read responses back
through the API.

## Decision

Only deliver to hosts on a configured allowlist (`Webhooks:AllowedHosts`; exact names or
`*.example.com`, which does not match the apex), and limit what a request can do or reveal:

- The URL must be absolute `http`/`https` (`https` only unless `Webhooks:RequireHttps=false`
  for the local demo), without user info or fragment. Host names are compared in punycode form.
- The check runs when a subscription is created or updated (`400` with a field error) **and again
  before every delivery attempt**, so removing a host from the allowlist stops existing subscriptions
  too (`Failed` with `host_not_allowed`, nothing is sent).
- Redirects are not followed; a `3xx` response is a failed attempt. Otherwise an allowed host could
  redirect to an internal one.
- The response body is never read, stored, logged or returned. Only the status code and a short,
  relay-generated error code (`http_503`, `timeout`, `connection_failed`) are recorded, so the API
  cannot be used as an oracle for internal services.
- Connect and total timeouts are bounded; cookies are not kept between requests.

## Consequences

- Strong, easy-to-audit protection: anything not explicitly allowed is unreachable.
- Onboarding a new subscriber host requires a configuration change. That is acceptable for an
  internal relay with known consumers, less so for a self-service public product.
- **Gap: DNS.** The allowlist is checked on the host name. An allowed name that resolves to a
  private or loopback address (misconfiguration or DNS rebinding) would still be reached. Closed by
  the connect-time IP check below.

## Alternatives considered

- **Blocklist of private IP ranges.** Necessary for self-service products, but easy to get wrong
  (IPv6, IPv4-mapped IPv6, alternative encodings, DNS rebinding between check and connect). It
  belongs at connection time, not at validation time.
- **IP check at connect time** via `SocketsHttpHandler.ConnectCallback`: resolve the name, reject
  private/loopback/link-local addresses, then connect to the verified address. This closes the DNS gap;
  it complements the allowlist rather than replacing it. Implemented, see the update below.
- **Egress proxy** (e.g. a dedicated forward proxy with its own policy). The most robust option for
  production, but heavy infrastructure for this project.

## Update (2026-11-18): connect-time IP check

Implemented in `WebhookConnectionGuard` (the webhook client's `ConnectCallback`):

- The host name is resolved inside the callback, **every** resolved address is classified, and the
  socket connects to exactly those addresses. There is no second lookup, so the answer cannot change
  between the check and the connection.
- A host with any non-public address is refused as a whole. The attempt fails permanently with
  `address_not_allowed` and nothing is sent; the address itself is logged for operators only.
- IPv4-mapped IPv6, NAT64 (`64:ff9b::/96`) and 6to4 forms are unwrapped or blocked, so
  `::ffff:169.254.169.254` is treated like `169.254.169.254`.
- `Webhooks:AllowPrivateNetworks=true` lets loopback and private networks through for the local
  demo and tests (the receiver runs on a Docker network or on `localhost`). Link-local addresses and
  cloud metadata and platform endpoints (`169.254.169.254`, `fd00:ec2::254`, Azure's public
  `168.63.129.16`, …) stay blocked regardless.
- The webhook client uses no proxy: a proxy would resolve and connect on its own, bypassing the check.
