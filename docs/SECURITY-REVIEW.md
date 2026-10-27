# Security review — 0.8.0

## Scope

This pass reviews the first-party ASP.NET Core orchestration/UI, account and role
boundaries, agent enrollment/authentication, browser/agent relays, remote identity
verification, source packaging and Debian service configuration. It retains the
working native agent and viewer protocol implementations and their required notices.

## Patched boundaries

| Area | Change | Verification |
|---|---|---|
| Delegated account management | Managers cannot reset, disable or take over accounts with greater permissions, assign a more privileged role, or grant capabilities they do not hold. Administrator protection remains in place. | Escalation/reset/role-grant rejection and permitted limited delegation tests. |
| Controller device | The management server's own enrolled device is marked from its authenticated connection address and hostname. Remote commands, files, screen/network tools and device changes require `devices.controller`, granted to Administrator by default. Changing reported metadata does not clear protection. | Persistent boundary and explicit delegation tests; remote entry points check the boundary. |
| Session replay | Web sessions have hashed server-side IDs and revocation state. Logout revokes the matching session; replayed cookies fail. Existing signed pre-upgrade cookies migrate during a bounded grace window, preserving current sign-ins. | Real-cookie replay test, independent-session test and legacy migration/revocation tests. |
| Live authorization | Interactive authentication and remote sessions revalidate every five seconds. Validation starts before remote connection setup, including while waiting for credentials. | Revocation closes a setup-waiting WebSocket without opening the target. |
| Cross-origin requests | Browser management and interactive sockets require a strict same-origin header. Agent sockets retain their certificate/ticket/bearer authentication. Forms keep antiforgery validation. | Cross-origin circuit rejection, CSRF and anonymous-route tests. |
| RDP identity | Certificate verification is enabled with a SHA-256 pin obtained through the authenticated agent or administrator configuration. Windows uses NLA; other RDP targets use TLS. No certificate-ignore or automatic first-use network trust is enabled. | Correct and deliberately incorrect pins tested against the Windows RDP service. |
| SSH identity | SSH host keys are checked before authentication. Public identity material comes from the authenticated agent or administrator configuration. | Correct and deliberately incorrect host keys tested against an SSH service. |
| Untrusted input | Authentication/enrollment request bodies, pre-authentication native frames, browser credential setup, agent metadata/chunks and protocol-worker instructions have bounds. Setup/negotiation has deadlines. Canceled native tunnel setup releases late resources. | Malformed input, destination classification and setup-cancellation regressions. |
| Direct destinations | Loopback, unspecified, link-local/metadata and multicast destinations are refused. Direct access to the controller's local addresses needs the controller capability. | IPv4, mapped IPv6 and link-local/multicast tests. |
| Private state | Data directories cannot be placed under the public web root. Supplemental-agent state is created with restrictive permissions before secrets are written. Restricted file roots reject directory links as well as file links. | Configuration/path checks and source review; filesystem restrictions still depend on the operating-system account. |
| Browser defense | CSP restricts scripts to the application origin, forbids script attributes and object embedding, restricts form submission and framing; HTTPS gets HSTS, cookies are Secure in production, and referrers do not carry enrollment URLs. | Header tests and real browser/viewer regressions. |
| Exposure/auditing | Connection attempts are rate-limited. Rejected security requests are recorded with source addresses, with bounded deduplication to prevent log flooding. Sign-in events include their source address. | Rate-limit configuration, audit checks and endpoint review. |
| Service isolation | The protocol worker uses a separate non-login account, cannot read controller configuration/state/source, and is restricted to loopback networking. Both services have read-only system files, no capabilities/new privileges, private temporary/device views and disabled core dumps. | Installer syntax and deployed service/user/path checks. |
| Packaging | Public source archives use an explicit source layout/configuration allowlist and exclude private state, environment files, runtime databases, identities and enrollment policy files. | Archive fixture verifies required source/notices and rejects private markers. |

## Remote host trust

An online registered agent reads only the target machine's public service identity:
the bound RDP certificate or SSH host public key. The result is stored per device,
protocol and port. Agentless access can reuse that verified identity while the agent
is offline; an unknown target fails closed.

An administrator can configure an independently verified identity under
`MeshAll:TrustedRemoteIdentities:DEVICE_ID:rdp:3389` (SHA-256 fingerprint) or
`MeshAll:TrustedRemoteIdentities:DEVICE_ID:ssh:22` (OpenSSH public host key).
Changing a network address does not disable verification. Certificate/key rotation
is refreshed through the authenticated agent or an administrator's verified config.
Nonstandard listeners or nondefault certificate/key locations need an explicit pin.
When an agent publishes multiple SSH host keys, selection follows the locally
installed codec library's algorithm preference order instead of assuming Ed25519.
Private user keys/passwords never belong in these public-identity settings.

## Preserved behavior

Account IDs, password hashes, role assignments, existing two-factor secrets and
agent identities are preserved during updates. Two-factor enforcement remains a
next-sign-in policy; required-enrollment sessions cannot use management functions.
RDP defaults remain encrypted per user and are decrypted only for the authenticated
interactive editor/viewer. They are not prerendered, exported or logged.

Native desktop recovery, deliberate local stop, consent/group policy and native
terminal/file/command protocols retain their working implementations. Bash adds
an explicit `/bin/bash` path; the existing POSIX shell type retains `/bin/sh`.

## Dependency and exposure checks

The npm dependency audit and .NET transitive vulnerability listing reported no
known advisories for the reviewed dependency sets. Debian security-library updates
are applied separately from source changes. The intended network listeners are
authenticated HTTPS management/agent traffic and the existing administrative SSH
service; the protocol worker is loopback-only.
The retained native agent also owns its UDP discovery/transport socket; this is
separate from the authenticated HTTP management interface and is recorded in the
host exposure review. No router, VPN, adapter or routing configuration is changed.

## Validation limits

This is a reviewed and tested hardening pass, not a proof that vulnerabilities
cannot exist. Retained native binaries/codecs, operating-system components and
upstream dependencies need continuing advisory review and updates. Live acceptance
covers the authorized Windows/Linux targets; macOS and unconfigured advanced
transports are not implied to be accepted. Router/NAT rules, external firewall
policy and unrelated services outside the authorized host are not established by
an application audit. Filesystem root checks supplement OS permissions and are not
an isolation sandbox for a hostile process that can change the same filesystem.

Required license/provenance notices and compatibility-sensitive protocol/file names
remain with retained components. First-party operational prose uses EndpointControl naming.
