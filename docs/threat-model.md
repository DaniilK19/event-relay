# Threat model

What could go wrong security-wise in EventRelay, what the code and configuration do about it,
and what is deliberately left out. EventRelay is a learning / portfolio project: the goal is a
design that would be sound in production, implemented as far as is reasonable for a demo, and
honest about the rest.

- [What we protect](#what-we-protect)
- [Who interacts with the system](#who-interacts-with-the-system)
- [Trust boundaries](#trust-boundaries)
- [Threats and mitigations at a glance](#threats-and-mitigations-at-a-glance)
- [The threats explained](#the-threats-explained)
- [What is deliberately not done](#what-is-deliberately-not-done)
- [Repository settings (not expressible in code)](#repository-settings-not-expressible-in-code)

## What we protect

| Asset | Why it matters |
| --- | --- |
| The network EventRelay runs in, and the cloud metadata endpoint | The dispatcher makes HTTP requests to URLs chosen by others. If those requests can reach internal services or the metadata endpoint, an attacker can act with the app's own network position and cloud identity. |
| Webhook signing secrets | Whoever knows a subscription's secret can forge webhooks that its receiver will trust. |
| Events, subscriptions and delivery history | Producers' data and the routing that decides who receives it. |
| API keys, database password | Access to everything above. |
| The build pipeline and dependencies | Code from third parties runs in CI and ends up in the container images. |

## Who interacts with the system

| Actor | Access |
| --- | --- |
| Producer (for example the flight simulator) | Publisher API key: sends events, reads their delivery status |
| Operator | Admin API key: everything, including subscriptions |
| Subscriber | Receives signed webhooks; never calls the API |
| Anyone else: the internet, the same Wi-Fi, another tenant of the cloud | No key |
| A compromised dependency or GitHub Action | Whatever CI or the running process can do |

## Trust boundaries

```mermaid
flowchart LR
    subgraph outside[Outside: internet / LAN]
        producer[Producer]
        operator[Operator]
        subscriber[Subscriber endpoint]
    end
    subgraph inside[EventRelay network]
        api[API]
        dispatcher[Dispatcher]
        db[(PostgreSQL)]
        sb{{Service Bus}}
        meta[[Cloud metadata<br/>169.254.169.254]]
    end
    producer -- "① X-Api-Key: publisher" --> api
    operator -- "① X-Api-Key: admin" --> api
    api --> db
    api --> sb
    sb --> dispatcher
    dispatcher --> db
    dispatcher -- "② signed webhook:<br/>allowlist + IP check" --> subscriber
    dispatcher -. "② must never reach" .-> meta
```

1. **Inbound, internet → API.** Every caller is authenticated and limited to its role.
2. **Outbound, dispatcher → URL from a subscription.** The most dangerous direction: the server
   acts on someone else's input from inside the network.
3. **Developer machine and GitHub → repository.** Secrets must never cross into git.
4. **Third-party code → CI and images.** Actions and packages run with our permissions.

## Threats and mitigations at a glance

| # | Threat | Mitigation here | Checked by |
| --- | --- | --- | --- |
| T1 | Credentials committed to git | No credentials in tracked files; `.env` and user secrets; compose requires variables instead of defaults; GitHub secret scanning and push protection | `docker compose config` fails without `.env` |
| T2 | Local stack reachable from the network | Published ports bound to `127.0.0.1`; SQL Server publishes none | `docker compose config` |
| T3 | Unauthenticated API | API keys with two roles; deny-by-default fallback policy; constant-time comparison | `AuthenticationTests` |
| T4 | SSRF through webhook URLs | Host allowlist on save and before every attempt; no redirects; no proxy; response body never read; generic error codes | `AllowlistTests`, `DeliveryTests` |
| T5 | DNS rebinding, allowed names that point inside | IP check in `ConnectCallback`, connect only to vetted addresses; metadata endpoints always blocked | `WebhookConnectionGuardTests` |
| T6 | Database reachable from all of Azure | PostgreSQL in a delegated subnet, no public endpoint, private DNS; TLS with certificate verification | `terraform validate` |
| T7 | Compromised GitHub Action or package | Actions pinned to commit SHAs; read-only workflow token; Dependabot; CodeQL; NuGet audit | CI, CodeQL |
| T8 | Forged or replayed webhooks | HMAC-SHA256 per subscription over timestamp and body; receiver checks timestamp and de-duplicates by the signed event id | `DeliveryTests`, sample receiver |
| T9 | Information leaks through errors | Response bodies never read or returned; relay-generated error codes; ProblemDetails without stack traces outside Development | `DeliveryTests` |
| T10 | Flooding the API (denial of service) | Request size limits only; **rate limiting not done** | – |

## The threats explained

### T1. Credentials in the git history

Anything pushed to a public repository should be treated as leaked. Bots scan GitHub for keys
within minutes of a push. Deleting the file later does not help: it stays in the history, in forks
and in clones. "Throwaway" demo passwords are a problem too: they get reused, copied into real
deployments, and they normalise the habit of committing credentials.

What is done here:

- No password, key or connection string with credentials in any tracked file. The only
  committed values are placeholders in `.env.example`.
- `docker-compose.yml` uses **required** variables (`${POSTGRES_PASSWORD:?…}`): without a `.env`,
  compose stops with an explanation instead of silently falling back to a well-known password.
- `dotnet run` takes credentials from user secrets, a file in the user profile outside the repository.
- Terraform generates the database password and API keys itself. State and `*.tfvars` are git-ignored.
- GitHub **secret scanning** detects known credential formats, and **push protection** rejects a
  push that contains one.

One exception: the Service Bus emulator's `SAS_KEY_VALUE`. It is a fixed, publicly documented value
that the local emulator requires, and it grants access to nothing outside that container.

### T2. Ports open to the local network

`ports: "5432:5432"` publishes on **all** network interfaces (`0.0.0.0`). On a laptop in a café,
an office or a hotel, anyone on the same network can connect to PostgreSQL with the demo password.
On a cloud VM, the whole internet can. On Linux, Docker writes its own firewall rules, which take
effect before tools like `ufw`, so a host firewall may not protect a published port.

Here, every published port is bound to `127.0.0.1` (`"127.0.0.1:5432:5432"`): reachable from this
machine only. SQL Server, which only the emulator uses, publishes no port at all.

### T3. An API anyone can call

Without authentication, anyone who can reach the API could:

- create a subscription and receive a copy of every event;
- delete or redirect other subscriptions;
- publish fake events that subscribers trust, because EventRelay signs them with valid HMACs;
- use subscriptions as a building block for SSRF (T4).

Here:

- API keys in the `X-Api-Key` header, with two roles. **Publisher** keys may send and read events;
  **admin** keys may also manage subscriptions. Each client gets the least it needs: the simulator
  only has a publisher key.
- **Deny by default.** A fallback authorization policy requires a key on every endpoint unless
  the endpoint explicitly opts out (`/health`, the OpenAPI document and the reference page). A new
  endpoint is protected even if its author forgets.
- Keys are compared as SHA-256 hashes with `CryptographicOperations.FixedTimeEquals`, so response
  timing reveals nothing about how close a guess was. Keys must be at least 32 characters, and each
  role accepts several keys so they can be rotated without downtime.
- `401` without a valid key, `403` for the wrong role. Keys are never logged.

Limits: keys are static shared secrets, and there is no tenant isolation (every admin sees every
subscription). See [What is deliberately not done](#what-is-deliberately-not-done).

### T4. Server-side request forgery (SSRF)

An attacker outside cannot reach the internal network, but the server can, and SSRF makes the
server send a request to an address the attacker chose. A webhook URL is exactly that kind of input.

The classic target is `http://169.254.169.254/`, the **instance metadata service** of cloud
providers. It is reachable from any VM or container without authentication and hands out, among
other things, temporary credentials for the machine's cloud identity. In the **Capital One breach
(2019)**, an attacker used SSRF through a misconfigured web application firewall to query AWS
metadata, obtained the role's credentials and downloaded data on about 100 million customers from
S3. The incident is one of the reasons AWS introduced IMDSv2. Other targets are services that
trust "anything from inside": `localhost:5432`, admin panels, internal APIs. Even without reading
responses, an attacker can map the network from the difference between "connection refused",
"timeout" and an HTTP status.

EventRelay applies several layers ([ADR 003](adr/003-webhook-allowlist.md)):

1. **Host allowlist** (`Webhooks:AllowedHosts`), checked when a subscription is saved and again
   before every attempt, so removing a host takes effect immediately.
2. **IP check at connect time** (T5).
3. **No redirects.** Otherwise an allowed host could answer `302 Location: http://169.254.169.254/`.
4. **No proxy.** A proxy would resolve and connect on its own, bypassing the IP check.
5. **The response body is never read**, stored or returned. The API cannot be used to read internal pages.
6. **Generic error codes** (`http_503`, `timeout`, `connection_failed`, `address_not_allowed`)
   instead of exception messages, to give as little signal for network mapping as possible.
7. URLs with credentials (`http://user:pass@host`) or non-HTTP schemes are rejected; HTTPS is
   required outside the local demo.

### T5. DNS rebinding and allowed names that point inside

The allowlist checks host **names**, but the connection goes to an IP **address**, and the name
→ address mapping is controlled by whoever runs the domain's DNS:

- If `*.customer.example` is allowed, the customer can point `evil.customer.example` at `10.0.0.5`
  or `169.254.169.254`.
- **DNS rebinding:** a record with a very short TTL answers with a harmless public address while
  the subscription is validated and with an internal address moments later, when the request is
  actually sent.

Fix: the check happens **at the moment of connecting**, on the address actually used.
`WebhookConnectionGuard` (the webhook client's `SocketsHttpHandler.ConnectCallback`) resolves the
name, classifies every address, refuses the host if any of them is not public, and connects the
socket to exactly the addresses it checked. There is no second lookup, so nothing can change in
between.

The classification also covers less obvious spellings of the same address: IPv4-mapped IPv6
(`::ffff:169.254.169.254`), NAT64 (`64:ff9b::a9fe:a9fe`), 6to4 and the deprecated IPv4-compatible
form. Private networks can be allowed for the local demo (`Webhooks:AllowPrivateNetworks`, because
the receiver runs on a Docker network), but **link-local addresses and cloud metadata endpoints
stay blocked in every configuration**, including the IPv6 ones that sit in private ranges
(`fd00:ec2::254` for AWS) and Azure's platform endpoint `168.63.129.16` (WireServer, DNS, health
probes), which is a public address and would otherwise pass as "internet".

### T6. A database open to "all of Azure"

A common shortcut, and the one an early draft of this Terraform code took, is a PostgreSQL firewall
rule from `0.0.0.0` to `0.0.0.0`. In Azure that means "allow access from Azure services". It sounds internal, but it allows
connections from **any IP address of any Azure datacenter**, including virtual machines of every
other Azure customer. An attacker only needs a free Azure account to reach the port; after that,
only the password protects the data.

Now PostgreSQL uses **VNet integration**. The server lives in a delegated subnet, has no public
endpoint (`public_network_access_enabled = false`), and its name resolves through a private DNS
zone linked to the virtual network. Only the Container Apps environment in the same network can
reach it. The password and TLS with certificate verification (`SSL Mode=VerifyFull`) remain as
additional layers.

### T7. Supply chain: actions and packages

CI runs third-party code, and GitHub Actions run with access to the repository and its secrets.
A reference like `uses: some/action@v4` points to a **tag**, and a tag is a movable pointer: if the
action maintainer's account is compromised, the attacker moves the tag, and every workflow that
uses it runs the new code on its next run.

This is what happened with **tj-actions/changed-files in March 2025** (CVE-2025-30066). Attackers
moved almost all of its version tags to a commit that dumped the CI runner's memory, including
secrets, into the build log. More than 20,000 repositories used the action, and the logs of public
repositories are public. Workflows pinned to a full **commit SHA** were not affected, because a SHA
identifies immutable content.

Here:

- Every action is pinned to a full commit SHA, with the version in a comment
  (`actions/checkout@3d3c42e… # v7.0.1`).
- **Dependabot** updates those pins (and NuGet packages, Docker images, Terraform providers), so
  pinning does not mean never updating. Every update goes through CI.
- Workflows get a **read-only token** (`permissions: contents: read`, `persist-credentials: false`),
  and CI holds no cloud credentials at all. Even a malicious step would find little worth stealing.
- **CodeQL** scans the C# code and the workflow files themselves (for example for script injection
  through pull request titles).
- **NuGet audit** fails the build on high or critical advisories; central package management with
  transitive pinning makes every version explicit.

### T8. Forged and replayed webhooks

A subscriber must be able to tell real webhooks from forged ones. Each subscription has its own
256-bit secret (shown once, rotatable). `X-Signature` is an HMAC-SHA256 over `"{timestamp}.{body}"`;
receivers recompute it over the raw bytes, compare in constant time, reject old timestamps
(replays) and de-duplicate by the event `id` taken from the verified body.

The last point matters because only the timestamp and the body are signed. A receiver that
de-duplicated by the `X-Event-Id` header could be fooled: whoever captured one request (from logs,
a TLS-terminating proxy) can replay it within the time window with a fresh header value, and the
signature still verifies. For the same reason a secret must never be shared between subscriptions,
or a request captured for one would be accepted by the other. The sample receiver does all of this;
it derives a separate secret for each of its endpoints from one configured value.

### T9. Leaking information through errors

Delivery results contain only status codes and relay-generated codes, never response content or
exception messages. Production error responses are ProblemDetails without stack traces. Addresses
refused by the IP check appear in operator logs, not in the API.

### T10. Flooding

Request bodies are capped (256 KiB, event `data` ≤ 64 KiB), and webhook concurrency and timeouts
are bounded. There is **no rate limiting**: a key holder can send as many events as the database
accepts. Listed below.

## What is deliberately not done

Out of scope for a demo, and what production would add:

| Gap | Risk | Production approach |
| --- | --- | --- |
| Webhook secrets are stored in plain text in the database (they must be readable to sign) | A database dump allows forging webhooks for every subscriber | Encrypt at rest with ASP.NET Core Data Protection and keys in Azure Key Vault, or switch to asymmetric signatures (Ed25519) so only a private key needs protection |
| The signature covers neither the delivery headers nor the target | Receivers must take the event id from the body and use one secret per subscription; one that trusts headers can be fooled by a replay | Sign `"{id}.{timestamp}.{body}"` as in the Standard Webhooks specification |
| No rate limiting or quotas | One key holder can flood the system; unauthenticated request floods | ASP.NET Core rate limiting per API key, plus Azure Front Door / WAF in front of the API |
| Static API keys, no tenants | A leaked key works until it is rotated; every admin sees every subscription | OAuth 2.0 client credentials with Entra ID, subscriptions owned by a tenant, audit log |
| Standard container images with a writable file system | Larger attack surface after a compromise | Chiseled (distroless) .NET images, read-only root file system, images pinned by digest, vulnerability scanning (Trivy) |
| No NuGet lock files | A dependency graph that can drift between builds | `RestorePackagesWithLockFile` and `--locked-mode` in CI |
| Secret detection relies on GitHub settings only | A secret in a fork or before push protection is enabled | gitleaks in CI and as a pre-commit hook |
| Password authentication for PostgreSQL | A password in Terraform state and in a Container Apps secret | Microsoft Entra authentication with the apps' managed identities (passwordless) |
| No egress control | A compromised container can call anywhere; the IP check covers webhooks only | Azure Firewall or NAT gateway with an egress allowlist, or a dedicated egress proxy (for example Smokescreen) |
| Secrets in Terraform state | Read access to state means read access to secrets | Key Vault references in Container Apps; secrets created outside Terraform |
| Service Bus public endpoint (Standard tier) | Reachable from the internet, although only with Entra ID tokens | Premium tier with a private endpoint |
| `/health` and the OpenAPI document are public | They reveal the API's shape (no data) | Acceptable; could be restricted to the internal network |

## Repository settings (not expressible in code)

To be enabled on GitHub (Settings → Code security, Settings → Branches):

- **Secret scanning** and **Push protection**.
- **Dependabot alerts** and **Dependabot security updates** (version updates are configured in
  [`.github/dependabot.yml`](../.github/dependabot.yml)).
- **Private vulnerability reporting**, used by [SECURITY.md](../SECURITY.md).
- **Code scanning** results from the CodeQL workflow.
- **Branch protection** for `main`: pull requests only, CI and CodeQL must pass, no force pushes.
- Optionally, the Actions policy that only allows actions pinned to a full commit SHA.
