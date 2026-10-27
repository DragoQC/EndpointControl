# Company-readiness review

EndpointControl's readiness is tracked by reproducible behavior and deployment
evidence. Passing this review does not certify every platform, deployment topology,
or retained dependency.

## Reliability fixes — 0.10.1

| Area | Reproduced/identified failure | Fix and regression coverage |
|---|---|---|
| Installer publication | Replacing the MSI before committing its manifest could destroy the last working download if the manifest write failed. | Immutable content-addressed artifacts; atomic manifest commit; failed-publication test restores the prior manifest and downloads the original bytes. |
| Installer concurrency | Selecting metadata and reading the mutable MSI later could mix two generations. | Snapshot reads and publication share a gate; hashes/names/bytes remain coherent during concurrent replacement; obsolete artifacts are bounded. |
| Installer revocation | A previously selected installer could still be read after its key was revoked, or a key could be revoked while compilation ran. | Revalidate the key after compilation and before/after reads. Existing 0.10.0 flat-file installers remain readable without republishing customer packages. |
| Installer tool failures | A compiler could report success but leave an empty, truncated or non-MSI output. | Check output bounds and the Compound File Binary header before publishing or returning the package; fault-injection compiler tests. |
| Authentication corruption | A stored hash missing its delimiter raised `IndexOutOfRangeException` during sign-in. Unknown-account verification also performed two PBKDF2 computations. | Validate the existing salt/digest format and fail authentication cleanly; use a fixed dummy hash so unknown accounts perform one verification. Existing account hashes are not rewritten. |
| International protocol data | Worker framing treated Unicode-character counts as UTF-8-byte counts; browser framing counted UTF-16 units and the retained parser mishandled supplementary characters. | Strict scalar-aware worker reader and first-party streaming browser framing. Tests include accented/CJK/supplementary characters, fragmented UTF-8/UTF-16, embedded separators and adjacent instructions. Retained codecs/rendering/input remain in use. |
| Terminal controls | Missing/null/string/fractional resize fields raised uncaught exceptions; a non-string `type` could disrupt ordinary input. | Recognize only reserved resize controls; validate integers; bound terminal dimensions; preserve non-control input and close malformed controls with a safe error. |
| Managed stream lifetime | A second disposal accessed an already-disposed cancellation source; concurrent server cleanup returned before its owner finished; canceled terminal setup could start a shell without returning an owner. | Shared idempotent disposal tasks; reject cancellation before allocation and release a terminal if cancellation arrives during creation. TCP peer-close, concurrent-close and pre-canceled setup tests. Collector failures still drain pending managed requests; duplicate request IDs and cancellation/completion races keep their existing owner. |
| Native tunnel cancellation | A connection could win between a completion-status check and cancellation, leaving its cleanup owner ambiguous. | Cancel the completion source first, then release any connection which already won. Delayed acceptance and failed-negotiation tests verify socket cleanup. |
| Native peer shutdown | Malformed authenticated JSON escaped the controlled disconnect path; callers waiting for core readiness could linger after disconnect. | Handle JSON failures as disconnects and cancel core-readiness waiters during peer disposal. |

The regression tests are in `AuthenticationReliabilityTests`,
`InstallerReliabilityTests`, `ProtocolReliabilityTests`,
`ManagedStreamReliabilityTests`, `NativeTunnelReliabilityTests`, and
`tests/guacamole-protocol.test.mjs`.

## Continuing update checks

- GitHub Actions runs the build/regression suite on pushes, pull requests, manual
  dispatch and a weekly schedule.
- npm advisory checks and transitive NuGet restore audits run in CI. Moderate or
  greater advisories fail the dependency checks; restore warnings remain subject
  to the repository's warnings-as-errors policy.
- Dependabot proposes weekly NuGet, npm and GitHub Actions updates. Updates go
  through review and the regression suite before application deployment.
- Native/codec/OS provenance and advisories need separate review; npm/NuGet audit
  results do not cover those binaries.

## Next acceptance gates

| Priority | Gap | Evidence needed |
|---|---|---|
| High | Native Windows MSI lifecycle and GPO rollout | Fresh install, EXE-to-MSI migration planning, same-version key renewal, version upgrade, rollback, reboot and uninstall on actual Windows/domain pilot machines. Wine/package-table checks are supplemental. |
| High | Backups and disaster recovery | Scheduled protected backups of SQLite, Data Protection keys, native identity and configuration, followed by a complete restore drill and account/device reconnect checks. |
| High | Load and long-lived operation | Controlled multi-agent/circuit soak, reconnect storms, large file/terminal workloads, bounded memory/disk use and clean shutdown/restart measurements. |
| High | Publisher signing/reputation | Validate signing/timestamping against the personalized executable/MSI format when the separately deferred distribution work resumes. |
| Medium | Operational visibility | Database/write readiness separate from process liveness, protocol-worker readiness, disk/certificate/backup alerts, and documented log/audit retention. |
| Medium | Authentication integrations | Provider-specific SSO, directory lifecycle and organization policy acceptance before treating them as implemented. |
| Medium | Additional platforms/transports | macOS installation and service recovery, Windows ARM64 installation, configured VNC/AMT/CIRA/WebRTC/routing workflows and their own negative controls. |
| Architectural | High availability | SQLite/single-controller lifecycle is the current operating model. Multi-controller failover/HA needs explicit storage, ownership and session-routing design. |

Reviews preserve existing accounts, device identities, privacy policies and
encrypted connection preferences. Test fixtures use disposable data and owned
processes; deployment uses source/database backups and preservation digests.
