# Functional parity and acceptance

The final architecture retains native agent/viewer technology, replaces the
orchestrator with C#, and stores management state in SQLite. Full feature parity
is the acceptance target. A successful baseline test against the old server is
not automatically a successful test against this new C# server.

| Workflow | Implementation | New C# orchestrator acceptance |
|---|---|---|
| Native agent authentication | C# certificate/nonce proof, TLS channel binding | Passed on Debian 13 and a Windows 11 system service |
| Enrollment and native executables | C# one-use/expiring keys and embedded-policy executables | Single-file Linux enrollment and Windows native GUI update/automatic reconnect passed; macOS artifacts packaged/hash-verified, OS installation acceptance pending |
| Windows x64 MSI / GPO | C# local MSI generation, standard Windows Installer service/upgrade actions | Real package compilation/table inspection/payload extraction passed; domain-based computer-startup rollout acceptance requires an AD pilot |
| Agent core delivery | C# platform core packer and SHA384 update protocol | Linux and Windows core delivery and execution passed |
| Desktop and optional approval | Retained native agent/core/viewer + C# relay | Windows sign-in/capture transitions, automatic keyboard/mouse recovery, reconnect and local sharing-bar stop passed; opt-in approval acceptance pending |
| Terminal / PowerShell | Native PTY + xterm.js + C# transport | Interactive agent terminal execution passed on Linux and Windows; separate PowerShell PTY acceptance pending |
| Commands / processes | C# orchestration + native agent runtime | Output, successful exit status and process inventory passed on Linux and Windows |
| Files | C# native file protocol | Linux and Windows listing, mkdir, binary upload/download comparison and cleanup passed |
| VNC | Native TCP transport + noVNC | A private Linux fixture previously passed; the current deployment is reported failing and further investigation is deferred |
| RDP | Native TCP transport or direct TCP + standard RDP worker | Windows authentication, visible rendering and reconnect passed through the native tunnel and direct orchestrator TCP |
| SSH | Native TCP transport + standard SSH worker | Linux authentication, visible terminal rendering and reconnect passed |
| AMT/KVM / CIRA | Retained native components; orchestration port in progress | Hardware/configuration acceptance pending |
| WebRTC | Native negotiation technology retained | Direct-path orchestration acceptance pending |
| TCP/UDP forwarding | Native routing technology retained | Redesigned workflow acceptance pending |
| Users, groups, scripts, sessions, audit | C# / Blazor / SQLite | UI tests passed; native integration checks ongoing |
| Profile 2FA / instance enforcement | Server-side TOTP, protected secrets, recovery codes and enrollment-only sessions | RFC 6238 and HTTP security tests passed; browser enrollment/recovery/next-login enforcement passed |
| Custom roles / capabilities | C# authorization policies and Blazor capability controls | Role creation/assignment/revocation and denied HTTP capabilities passed |
| Device inventory | Native core collector and C# validated snapshots | Live Linux and Windows reports passed; Windows logical CPU count and memory matched independent CIM queries |
| Recording/playback | Orchestration/UI integration in progress | Acceptance pending |
| SSO / tenants / mobile agents | Orchestration/UI integration in progress | Provider/platform acceptance pending |

The matrix deliberately distinguishes retained technology, a ported control
path, and end-to-end verification. No feature is declared fully verified merely
because its button or source component exists.

## Live acceptance evidence — 2026-10-22

The test deployment runs the C# orchestrator and its loopback Guacamole/FreeRDP
protocol worker on Debian 13. No original Node.js orchestrator participates.
The Windows target runs the packaged native x64 agent as the `MeshAllAgent`
system service. The previous interactive identity is a distinct device record.

- `tests/acceptance.py` passed commands, processes, files and terminal execution
  on Linux and Windows. Binary transfer compares approximately 133 KB containing
  every byte value, then removes the test file and directory.
- Windows native desktop rendered and reconnected. A controlled WinForms window
  received a unique marker through native desktop keyboard input; a native mouse
  click saved it and closed the fixture. The command channel independently read
  the exact marker and removed the temporary file.
- RDP through both the Windows native TCP tunnel and direct orchestrator TCP,
  plus SSH through the Linux native TCP tunnel, passed nonblank composited-pixel
  checks and reconnects with the corrected renderer. VNC passed the same
  display/reconnect checks against Xvfb/x11vnc.
- A negative-control RDP run against the unfixed renderer failed specifically
  because the composited display was blank. The corrected renderer passed; this
  guards against declaring success merely because a hidden canvas has dimensions.
- Windows unattended installation was verified with
  `MeshAllAgent.exe -fullinstall --copy-msh=1`. Omitting the policy-copy flag can
  leave a running service without its server configuration. Interactive and
  system-service certificate identities can differ, so a one-device enrollment
  key should first be used by the intended installed service.

These observations verify the workflows listed, not every protocol option or
platform. Locale-specific input, clipboard, multi-monitor/session selection,
negative exit-status semantics, dedicated PowerShell PTY, opt-in consent policy,
and the advanced workflows above still need their own acceptance coverage.
See `tests/README.md` for repeatable live checks.

## Account security, roles and inventory — 0.3.0

`tests/MeshAll.Tests` covers RFC 6238 vectors, second-factor enforcement before
issuing a full session, recovery/receipt replay, challenge attempt limits,
account-version revocation, protected secrets, CSRF, custom capabilities and
native policy-trailer framing. Browser acceptance on an isolated HTTPS instance
verified custom role creation/assignment, authenticator QR enrollment, recovery
sign-in, next-login instance enforcement and role-change session revocation.

A single downloaded Linux executable enrolled against that instance without an
external policy file. Its native collector reported actual hardware and network
data; commands, processes, binary files and interactive terminal checks passed.

On the Windows test target, the single downloaded `MeshAllAgent.exe` opened the
native installer with the instance and group already populated. Its **Update**
action elevated normally, installed/restarted the service and automatically
reconnected. The installed service executable retained the native policy trailer;
no extracted ZIP or manually placed policy participated in that workflow.

Windows reported six logical CPUs and 25,747,271,680 bytes of visible memory,
matching independent `Win32_ComputerSystem` / `Win32_OperatingSystem` CIM queries.
OS/build, LAN address and MAC were populated. Commands, process inventory, binary
files, interactive terminal and desktop/reconnect passed after deployment. The
capability-restricted desktop relay also preserves the retained viewer's initial
latency message; regression tests reject switching it to terminal/file protocols.

## Viewer, navigation and removal — 0.3.1, updated in 0.7.0

The original full-window viewer remains available. As of 0.7.0, the device's
Desktop tab embeds the shared viewer directly, auto-connects and fits the available
space without an iframe or nested scroll area. `tests/ui_regressions.py` exercises
inline/standalone views, tab disposal/reopening, landscape/portrait remote screens,
browser resizing, fullscreen and scaled pointer
coordinates using an isolated simulated agent. It also covers light/dark navigation
through Settings and static Security, including the Security page's theme button.

Device removal hides the website entry until an authenticated reconnect, without
discarding registration or group policy. Browser tests verify that heartbeats do
not restore it. Server tests cover native return with a revoked enrollment key,
retained blocks, managed-agent return, legacy schema migration, and adding the new
Administrator capability without changing account records.

## Desktop recovery, Web-VNC and typed scripts — 0.4.0

Native viewer transport recovery preserves the view and requested control mode.
Input availability follows current server permissions and receipt of a real desktop
frame. Windows sharing-bar helper termination/session changes are distinct from an
explicit local close. Node and real native-viewer browser-protocol tests cover
recovery, input rebinding, permission loss, consent/local-user stop and cancellation.

Web-VNC now uses the configured/default port and prompts only when RFB requests
authentication through the retained RFB / noVNC credential negotiation. Native
desktop requires no external VNC setup; Web-VNC still requires an existing service.
Browser fixtures verify both RFB None and password authentication.

Saved-script types choose PowerShell, Command Prompt or Unix shell. Browser tests
verify editor/selection/request routing. Live Windows probes and the deployed saved
script library verified real `.cmd` batch labels and extension, PowerShell
`param(...)`/`.ps1`, Unicode output and explicit exit statuses 7 and 9. Unix file
execution/exit and legacy-schema tests run in the server test suite.

### Live follow-up — 2026-10-23

- The same native viewer progressed from the Windows lock/sign-in screen to the
  signed-in desktop with control automatically enabled. A controlled WinForms
  textbox received a unique keyboard marker, and a native mouse click saved it.
  The independent command channel verified the exact file contents and cleaned
  up the fixture. Real agent-service restart recovery and another independent
  keyboard/mouse verification passed without toggling input.
- Closing the actual Windows sharing bar reports a local-user stop and does not
  reconnect automatically. Three consecutive stop / deliberate new Connect cycles
  passed. Native input/capture pipes are detached immediately; teardown waits for
  viewer acknowledgement with bounded fallback cleanup. The detached readable
  stream resumes so native end notifications and connection counts remain valid.
- Deployed saved `.cmd` and `.ps1` scripts selected their interpreters automatically
  and returned actual exit codes 7 and 9. The PowerShell case preserved `param`,
  `$PSCommandPath` and Unicode output; the batch case preserved labels and `%~x0`.
- The password-authenticated VNC fixture passed visible framebuffer and reconnect
  checks through the real Linux agent's native TCP tunnel.
- Existing account IDs, names, password hashes, roles, disabled status, versions
  and protected authenticator secrets matched the pre-update snapshot.

## Readability, reusable views and device metrics — 0.5.0

First-party C# now uses explicit local types, expanded control flow and consistent
formatting. Razor operational views share typed tables, paging/search controls,
summary cards and resource meters. First-party JavaScript, CSS and Python are
formatted for editing; retained native JavaScript still uses ES5 syntax.

Database list pages fetch at most 50 rows. Sessions and audit search the full
matching history before applying the page; dashboard counters use aggregate
queries rather than loading session records. Explicit audit downloads stream
their rows. Device file/process/interface/disk views page their requested snapshots.

The connected-device dashboard presents CPU, memory and storage usage percentages.
In 0.5.1 these are equal-weight per-device averages. Timestamps prevent partial reports from making
old samples current. Missing values stay unknown, and stale samples are excluded
after two minutes. Device Overview includes CPU, memory and volume capacity.

- 41 .NET tests passed, including bounded database reads, history search beyond
  250 sessions, weighted aggregates, stale/malformed samples and visibility filters.
- 22 Node tests passed, including collector cancellation, CPU counter deltas,
  zero-versus-missing samples and valid disk rows despite an inaccessible mount.
- A disposable browser run with 263 sessions verified 50-row pages, changing page
  size, older-history search, the other paged management lists, resource alerts,
  per-device readings, mobile layout and dark mode.
- Existing desktop, navigation/theme/removal and typed-script browser regressions
  passed against the refactored views.
- A native Linux agent delivered live CPU, memory and disk samples to the preview.
  Filesystem collection retained readable volumes when `df` also reported an
  inaccessible mount.

## Device averages and app-wide spacing — 0.5.1

Headline usage percentages average the reporting computers equally. Capacity
totals are secondary, and **Where to focus** flags individual full volumes and
overloaded machines. A 99%-full system volume remains visible there even when
that computer has another mostly empty volume.

Page gutters, panels, forms, table cells, paging controls, resource cards and
operational views use shared spacing tokens. Nested panel margins no longer add
to grid gaps, mobile checkbox groups use the available width, and editor fields
have consistent label/control/section spacing. Desktop, tablet and mobile
screenshots are used to review the resulting layouts.

The 0.5.1 verification includes 43 .NET tests, the existing 22 Node lifecycle and
collector tests, and browser checks for equal-device averages, paged lists,
desktop viewing and theme navigation. Spacing review covers page/editor/table
screens at desktop (1440px), tablet (768px) and mobile (390px) widths.

## Concise copy and English/French localization — 0.6.0

Decorative headings, slogans and redundant architecture copy were removed from
operational pages. Navigation, forms, tables, statuses, dialogs and application
validation messages use English/French .NET resources. The top-bar language
selector persists a culture cookie and reloads the current local page; sign-in
and static security pages use the same selection.

52 .NET tests pass, including French resource/placeholder parity, formatting,
language-cookie persistence, antiforgery and redirect validation. Bilingual
browser checks cover pages/editors, anonymous sign-in, static security, French
script selection with canonical identifiers, raw output preservation, localized
desktop connection states and desktop/tablet/mobile themes. Existing dashboard,
pagination, desktop, navigation and device-removal browser regressions also pass.

## Inline tools and personal RDP defaults — 0.7.0

Device tabs and remote-tool cards embed one shared `RemoteViewer`, while the
standalone route remains a thin wrapper. Inline Desktop auto-connects, fits the
available viewport, supports fullscreen and disposes its session on tab changes.
Owner-scoped cleanup and disposal guards prevent old views from closing or starting
a replacement session. Workspace naming was removed and the language selector
reserves explicit space for its arrow.

RDP defaults belong to the signed-in user and require current RDP permission.
Passwords use a separate, user-bound Data Protection purpose, populate only the
authenticated interactive editor/viewer, remain masked, and never appear in
prerendered HTML or audit exports. Defaults can be updated or cleared independently
of login credentials. Sign-in/second-factor handlers, local administrator commands
and shared database helpers now have dedicated source files.

Verification: release build without warnings/errors, 55 .NET tests and 22 Node
tests. Disposable browser runs cover save/reload/update/clear, English/French
inline/standalone prefill and responsive preferences; inline/standalone display
fit, portrait/resizing/fullscreen/pointer scaling and tab return; retained native
protocol recovery/local stop; typed scripts; isolated VNC negotiation; localization;
and dashboard/pagination regressions. Ciphertext-copy tests reject cross-user decryption,
and revoked/stale accounts cannot read or change saved credentials.

## Persistent fullscreen controls and viewer lifecycle — 0.7.1

Fullscreen is a workspace action, available before connecting and after either a
deliberate or remote disconnect. Its explicit exit label and accessible pressed
state follow actual browser fullscreen. Disposing the viewer exits its fullscreen
element. Browser fullscreen refusal produces a localized, retryable message.

The Razor view has a C# code-behind. Browser transports now have focused native
desktop, managed desktop, terminal, VNC and Guacamole modules, backed by separate
workspace and connection lifetimes. Owner-scoped commands, stale-event guards,
idempotent resource cleanup, vendor-load retry and disposable display/input nodes
replace the monolithic client dispatch/cleanup block.

Verification includes a warning-free release build, 55 .NET tests and 25 Node
tests. English/French browser checks exercise inline/standalone Desktop/Terminal
and PowerShell, disconnected fullscreen exit/re-entry, connection and asset-load
failure/recovery, transport closure, browser-initiated exit, navigation disposal
and 1440/768/390px layouts. Native viewer recovery/local stop, typed scripts, VNC
negotiation, masked RDP defaults and localization regressions also pass.

Dashboard labels, titles and accessibility text now use device-focused terminology,
including Average CPU, Average memory and Average storage. The device table,
resource summary type, CSS selectors, tests and documentation use matching names.
The equal-device calculations and reporting thresholds are preserved.

## Management layout and security boundaries — 0.8.0

Native desktop/terminal/PowerShell tools appear first, with horizontal icon/text
cards. Bash is a distinct saved-script interpreter using `/bin/bash` with `.sh`
filenames; POSIX Shell retains `/bin/sh`. Shared `SelectField<TValue>` controls
provide one chevron/padding implementation and optional full width. Personal RDP
defaults sit beside the two-factor forms on Security, outside server Settings.
Logs combines sessions and audit history with independent capability checks.

Security changes cover server-revoked sign-ins, bounded migration of existing
cookies, delegated administration, controller-device isolation, socket origins,
connection setup/input limits, verified RDP/SSH host identities, audited rejections,
source archive exclusions and separate/sandboxed protocol-worker identity.
Correct and incorrect public identities have been checked against real RDP/SSH
services. The native agent/viewer implementations retain their working protocols.
See `SECURITY-REVIEW.md` for findings, controls, evidence and remaining audit limits.

## Native screen approval — 0.8.2

Screen approval has a bounded preflight before the native desktop tunnel opens,
as well as RDP/VNC/AMT agent-assisted screen transports. The agent shows a native
Yes/No dialog in the active user session and returns a nonce-, actor-, protocol-
and session-bound single-use grant. An empty console session cannot hide the
prompt from the active user. The desktop capture session follows the approved
session rather than untrusted client options.

The missing avatar-response compatibility handler is restored, with an agent-side
fallback so optional user images cannot block consent. Input is attached only
after consent. Denial, prompt failure, timeout and request cancellation close the
prompt and do not start automatic viewer retries. Unattended policy and deliberate
local sharing-stop behavior remain distinct.
