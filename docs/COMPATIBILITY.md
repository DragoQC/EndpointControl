# Functional parity and acceptance

The final architecture retains native agent/viewer technology, replaces the
orchestrator with C#, and stores management state in SQLite. Full feature parity
is the acceptance target. A successful baseline test against the old server is
not automatically a successful test against this new C# server.

| Workflow | Implementation | New C# orchestrator acceptance |
|---|---|---|
| Native agent authentication | C# certificate/nonce proof, TLS channel binding | In live testing |
| Enrollment and native packages | C# one-use/expiring keys, native policy/ZIP | In live testing |
| Agent core delivery | C# platform core packer and SHA384 update protocol | In live testing |
| Desktop and visible approval | Retained native agent/core/viewer + C# relay | Windows target acceptance pending |
| Terminal / PowerShell | Native PTY + xterm.js + C# transport | Native target acceptance pending |
| Commands / processes | C# orchestration + native agent runtime | Native target acceptance pending |
| Files | C# native file protocol | Native target round-trip pending |
| VNC | Native TCP transport + noVNC | VNC target acceptance pending |
| RDP | Native TCP transport + standard RDP worker | RDP credentials/target acceptance pending |
| SSH | Native TCP transport + standard SSH worker | Target acceptance pending |
| AMT/KVM / CIRA | Retained native components; orchestration port in progress | Hardware/configuration acceptance pending |
| WebRTC | Native negotiation technology retained | Direct-path orchestration acceptance pending |
| TCP/UDP forwarding | Native routing technology retained | Redesigned workflow acceptance pending |
| Users, groups, scripts, sessions, audit | C# / Blazor / SQLite | UI tests passed; native integration checks ongoing |
| Recording/playback | Orchestration/UI integration in progress | Acceptance pending |
| SSO/MFA / tenants / mobile agents | Orchestration/UI integration in progress | Provider/platform acceptance pending |

The matrix deliberately distinguishes retained technology, a ported control
path, and end-to-end verification. No feature is declared fully verified merely
because its button or source component exists.
