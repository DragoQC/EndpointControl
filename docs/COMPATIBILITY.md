# Functional parity and acceptance

The final architecture retains native agent/viewer technology, replaces the
orchestrator with C#, and stores management state in SQLite. Full feature parity
is the acceptance target. A successful baseline test against the old server is
not automatically a successful test against this new C# server.

| Workflow | Implementation | New C# orchestrator acceptance |
|---|---|---|
| Native agent authentication | C# certificate/nonce proof, TLS channel binding | Passed on Debian 13 and a Windows 11 system service |
| Enrollment and native executables | C# one-use/expiring keys and embedded-policy executables | Single-file Linux enrollment and Windows native GUI update/automatic reconnect passed; macOS artifacts packaged/hash-verified, OS installation acceptance pending |
| Agent core delivery | C# platform core packer and SHA384 update protocol | Linux and Windows core delivery and execution passed |
| Desktop and optional approval | Retained native agent/core/viewer + C# relay | Windows frames, keyboard/mouse input and reconnect passed; opt-in approval acceptance pending |
| Terminal / PowerShell | Native PTY + xterm.js + C# transport | Interactive agent terminal execution passed on Linux and Windows; separate PowerShell PTY acceptance pending |
| Commands / processes | C# orchestration + native agent runtime | Output, successful exit status and process inventory passed on Linux and Windows |
| Files | C# native file protocol | Linux and Windows listing, mkdir, binary upload/download comparison and cleanup passed |
| VNC | Native TCP transport + noVNC | Password authentication, visible framebuffer and reconnect passed on a private Linux fixture |
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
