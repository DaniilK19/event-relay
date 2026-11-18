# Security policy

EventRelay is a learning / portfolio project, not a supported product. There are no releases;
only the `main` branch is maintained, on a best-effort basis.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Report it privately through GitHub:
**Security** tab → **Report a vulnerability** (private vulnerability reporting).

Helpful details:

- the affected part (API, dispatcher, Terraform, workflows…) and the commit you looked at;
- steps to reproduce or a proof of concept;
- what an attacker could achieve.

You can expect an acknowledgement within about a week. Fixes land on `main`, and the report is
credited unless you prefer otherwise.

If you come across a **real credential** in this repository or its history, report it the same way.

## Scope

In scope: the code and configuration in this repository: `src/`, `infra/terraform/`, `.github/`,
`Dockerfile`, `docker-compose.yml`.

Known and intentional, so not vulnerabilities:

- placeholder values in `.env.example`;
- the Service Bus emulator key `SAS_KEY_VALUE`: a fixed, publicly documented value that the local
  emulator requires;
- demo-only settings in `docker-compose.yml` and `appsettings.Development.json`: plain-HTTP
  webhooks, `Webhooks:AllowPrivateNetworks=true`, short retry delays;
- the sample apps in `samples/` (for example the deliberately flaky endpoint);
- the gaps listed under [What is deliberately not done](docs/threat-model.md#what-is-deliberately-not-done)
  in the threat model.

## Security design

- [docs/threat-model.md](docs/threat-model.md): assets, trust boundaries, each threat explained,
  mitigations and accepted risks.
- [ADR 003](docs/adr/003-webhook-allowlist.md): SSRF protection for webhooks.
- README, [Security considerations](README.md#security-considerations): a short summary.
