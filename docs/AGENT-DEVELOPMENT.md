# Native agent and viewer development

The native agent remains C/C++ (`components/agent-native`) and its runtime
modules remain JavaScript (`components/agent-core`). The C# orchestration server
implements the compatible control and relay protocols directly. The original
JavaScript server is not part of the runtime.

## Edit locations

- C# native authentication/core delivery: `Services/NativeAgentHub.cs`,
  `NativeCoreBuilder.cs`.
- C# agent orchestration/file/command transport: `Services/NativeAgentPeer.cs`.
- C# relays: `NativeTunnelHub.cs`, `AgentGateway.cs`, `NetworkGateway.cs`.
- On-PC consent/remote behavior: `components/agent-core/meshcore.js`.
- Windows consent window: `components/agent-native/modules/win-userconsent.js`.
- Browser desktop renderer: `components/viewers/scripts/`.
- Browser adapters/UI: `src/MeshAll.Server/wwwroot/app.js`, Razor components.

## Windows native build

Use Visual Studio 2022 Build Tools, C++ desktop development, MSVC v143 and the
Windows SDK:

```powershell
msbuild components\agent-native\MeshAgent-2022.sln /m /p:Configuration=Release /p:Platform=x64
```

The architecture filename is used by the native build/update tooling; packages
and policies expose `MeshAllAgent.exe` / `MeshAllAgent` to users. The agent
installer's display name/service/file name come from the generated policy.

## Linux native build

The included makefile documents dependencies and architecture IDs:

```bash
make -C components/agent-native linux ARCHID=6 -j2
```

`KVM=0` is a headless diagnostic build, not a desktop-capable release. Baseline
native binaries included for development are SHA384-verified against their
published provenance. Rebuilt MeshAll artifacts should replace those baseline
files through the same reviewed package/update process.

## Agent-side core

`NativeCoreBuilder` constructs platform-specific cores from local source and
computes their SHA384 hashes. The C# server delivers them through the native
core-update protocol. Changes to your local core/modules therefore reach your
enrolled agents through MeshAll, without editing a separate server installation.

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
