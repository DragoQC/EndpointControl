# MeshAll

**C# orchestration · Blazor Server + Tailwind UI · SQLite · proven native agents/viewers**

MeshAll modernizes the management/control plane while retaining native remote
access technology in the languages suited to it. The server is ASP.NET Core/C#;
it does **not** run the original MeshCentral Node.js orchestrator underneath.
The native C/C++ agent, its agent-side JavaScript modules, and working browser
viewers are maintained in this repository with their upstream licenses.

## Architecture

| Path | Purpose |
|---|---|
| `src/MeshAll.Server` | C# orchestrator, native-agent authentication/core delivery, relays, SQLite, Blazor UI |
| `src/MeshAll.Shared` | Shared management models |
| `components/agent-native` | Editable C/C++ agent source |
| `components/agent-core` | Agent-side runtime scripts and native modules |
| `components/viewers` | Retained native desktop/AMT browser technology |
| `components/agent-binaries` | Verified baseline native binaries and provenance |
| `src/MeshAll.Agent` | Additional .NET agent/protocol implementation and diagnostics |
| `install.sh` | Debian 13 server installer |

MeshAll's C# server directly implements native agent challenge/certificate
authentication, enrollment, core-module delivery and remote session control.
Device groups, users/roles, inventory, sessions, scripts and audit use SQLite.
Node.js is a **UI build tool**, not the application server.

## Install inside an existing Debian 13 machine

As root:

```bash
curl -fsSL https://raw.githubusercontent.com/DragoQC/MeshAll/main/install.sh | bash
```

The public repository is <https://github.com/DragoQC/MeshAll>. Local source:

```bash
bash install.sh --source "$PWD" --host YOUR_SERVER_IP
```

To retire an old MeshCentral server after MeshAll passes its health check:

```bash
curl -fsSL https://raw.githubusercontent.com/DragoQC/MeshAll/main/install.sh | bash -s -- --remove-legacy
```

The installer creates:

- **HTTPS 5188:** `meshall.service`, the C# orchestrator and Web UI.
- **Loopback 4822:** optional Apache Guacamole/FreeRDP protocol worker for RDP/SSH.
- `/opt/meshall/source`: editable source checkout.
- `/etc/meshall`: private configuration/HTTPS identity.
- `/var/lib/meshall/server`: SQLite, native-agent identity and persistent state.
- `/opt/creds.txt`: root-only initial `admin` account and randomly generated password.

The installer creates only the initial `admin` account with a unique random
password saved in `/opt/creds.txt` (mode 0600). Existing accounts are preserved
during updates. Direct development runs retain `/setup` with the one-time key in
the data directory. No shared default password is shipped. Enroll PCs from **Devices → Add device**; the native
package contains `MeshAllAgent` and its `.msh` policy. Windows uses the
`MeshAllAgent` service identity and **MeshAll Agent** display name.

Updates:

```bash
meshall-update --pull    # Fast-forward a clean checkout from your GitHub
meshall-update --local   # Rebuild your own local edits
```

`--check` validates the target without changing it. `--without-rdp-worker`
omits the optional protocol dependency for environments that do not use it.

## Develop

Requires .NET SDK 10 and Node.js 22+ for Tailwind/rendering asset preparation:

```bash
npm --prefix src/MeshAll.Server ci
npm --prefix src/MeshAll.Server run build
dotnet build MeshAll.slnx
dotnet run --project src/MeshAll.Server
```

Default dev URL: `http://127.0.0.1:5188`. Native agent connections require the
configured HTTPS certificate; the Debian installer provisions it. Set
`MESHALL_CONNECTION_FILE` to a private JSON configuration file outside Git when
running a custom HTTPS development host.

## User privacy

Groups expose **Ask the user before showing their screen**, notification, and a
connection toolbar. New groups use the original unattended default; prompting
is optional and enabled explicitly. The retained agent core
has MeshAll modifications for fresh per-session approval, no remembered five-
minute approval when strict mode applies, and denial on timeout. RDP/VNC/AMT
screen transports request agent approval before opening when the group policy
requires it.

## Parity and verification

**100% functionality is the acceptance target.** Retaining working low-level
technology is intentional; it does not automatically prove complete parity of
the new C# orchestration/UI. `docs/COMPATIBILITY.md` tracks implementation and
end-to-end verification per workflow. This development version must not be
represented as fully verified until that matrix is complete.

## License

MeshAll's original C#, Razor, adapters and scripts are **MIT**. Third-party
agent/viewer code, xterm.js, noVNC, Guacamole, .NET and other dependencies retain
their own licenses. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.
