# Native agent and viewer development

The native agent remains C/C++ (`components/agent-native`) and its runtime
modules remain JavaScript (`components/agent-core`). The C# orchestration server
implements the compatible control and relay protocols directly. The original
JavaScript server is not part of the runtime.

## Edit locations

- C# native authentication/core delivery: `Services/NativeAgentHub.cs`,
  `NativeCoreBuilder.cs`.
- C# agent orchestration/commands: `Services/NativeAgentPeer.cs`; native file
  operations: `NativeAgentPeer.Files.cs`.
- C# relays: `NativeTunnelHub.cs`, `AgentGateway.cs`, `AgentGateway.Desktop.cs`,
  `NetworkGateway.cs`.
- On-PC remote behavior: `components/agent-core/meshcore.js`.
- Approval/session binding: `modules_meshcore/meshall-screen-consent.js`; native
  Windows dialog and branding: `meshall-consent-dialog.js`, `meshall-consent-logo.js`.
- Browser desktop renderer: `components/viewers/scripts/`.
- Browser bootstrap: `src/MeshAll.Server/wwwroot/app.js`; transport adapters:
  `wwwroot/remote/`; UI and lifecycle: `RemoteViewer.razor`, `RemoteViewer.razor.cs`.

## Windows native build

Use Visual Studio 2022 Build Tools, C++ desktop development, MSVC v143 and the
Windows SDK:

```powershell
msbuild components\agent-native\MeshAgent-2022.sln /m /p:Configuration=Release /p:Platform=x64
```

The architecture filename is used by the native build/update tooling; packages
and downloads expose `EndpointControlAgent.exe` / `EndpointControlAgent` to users. The agent
installer's display name/service/file name come from the generated policy.

## Linux native build

The included makefile documents dependencies and architecture IDs:

```bash
make -C components/agent-native linux ARCHID=6 -j2
```

`KVM=0` is a headless diagnostic build, not a desktop-capable release. Baseline
native binaries included for development are SHA384-verified against their
published provenance. Rebuilt EndpointControl artifacts should replace those baseline
files through the same reviewed package/update process.

## Agent-side core

`NativeCoreBuilder` constructs platform-specific cores from local source and
computes their SHA384 hashes. The C# server delivers them through the native
core-update protocol. Changes to your local core/modules therefore reach your
enrolled agents through EndpointControl, without editing a separate server installation.

`modules_meshcore/meshall-inventory.js` collects device hardware, interfaces,
memory/CPU, disks and uptime. `NativeInventory.cs` validates/merges its reports;
`ReportedAt` is distinct from the last control-channel contact. The collector
uses read-only OS interfaces and does not change adapters, VPNs or routes.

## Single-file distribution

`NativeAgentExecutable.cs` appends an embedded policy to the original platform
binary using the native trailer: UTF-8 policy, four-byte big-endian policy length,
then the 16-byte `exeMeshPolicyGuid` from `agentcore.c`. License/notice text is
embedded ahead of that policy. Do not replace this with a ZIP or a launcher that
requires a second downloaded configuration file.

The Windows native GUI's Install / Update action installs that executable. Its
embedded policy remains with the installed service, so the first service start
can connect directly to the selected instance. Linux/macOS use the same policy
trailer with their native install command. `tools/import-native-agents.py`
reproduces the SHA384-pinned macOS baseline imports.

The strict-consent flag disables remembered approval. Network-screen transports
use the nonce-bound `meshall-consent` agent request before their TCP session.
Actual Windows approval/denial, locked-session behavior, and native executable
updates require the corresponding device acceptance tests.

`meshall-notifybar.js` wraps the retained Windows sharing bar's process lifecycle.
It respawns the helper after session changes and reports only its explicit close
button as a local user stop. `meshall-desktop-ended` control messages prevent the
browser recovery loop from reopening a deliberately ended session. The browser's
`desktop-session.mjs` reprobes permissions, waits for actual frames, and reapplies
requested input when restoring a transport.
