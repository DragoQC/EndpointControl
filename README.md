# EndpointControl

<img src="src/MeshAll.Server/wwwroot/favicon.svg" alt="EndpointControl" width="72" height="72" />

**C# orchestration · Blazor Server + Tailwind UI · SQLite · proven native agents/viewers**

EndpointControl modernizes the management/control plane while retaining native remote
access technology in the languages suited to it. The server is ASP.NET Core/C#.
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

EndpointControl's C# server directly implements native agent challenge/certificate
authentication, enrollment, core-module delivery and remote session control.
Device groups, users/roles, inventory, sessions, scripts and audit use SQLite.
Node.js is a **UI build tool**, not the application server.

## Install inside an existing Debian 13 machine

As root:

```bash
curl -fsSL https://raw.githubusercontent.com/DragoQC/EndpointControl/main/install.sh | bash
```

The public repository is <https://github.com/DragoQC/EndpointControl>. Local source:

```bash
bash install.sh --source "$PWD" --host YOUR_SERVER_IP
```

To retire a legacy server after EndpointControl passes its health check:

```bash
curl -fsSL https://raw.githubusercontent.com/DragoQC/EndpointControl/main/install.sh | bash -s -- --remove-legacy
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
the data directory. No shared default password is shipped.

## Install an agent: one executable

Open **Devices → Add device**, create an enrollment key, and download the
executable for the target platform. The server address, group and enrollment
policy are embedded in that file using the native agent's embedded-policy format.

- **Windows:** run `EndpointControlAgent.exe` and use the native **Install / Update**
  action. It installs/starts the service and connects automatically, using the
  retained native installation path. The service identity is
  `MeshAllAgent` for installed-agent compatibility; its display name is **EndpointControl Agent**.
- **Linux/macOS:** download the platform's single `EndpointControlAgent` executable,
  then run `chmod +x EndpointControlAgent; sudo ./EndpointControlAgent -fullinstall`.

No ZIP extraction, companion policy file or manually entered server settings are
needed. The installed agent creates its own local state normally. Native notices
are embedded in the executable and available at `/licenses/agent`.

Install the background service before using an interactive agent with a
one-device key: system-service and interactive certificate identities can differ.

Server updates:

```bash
meshall-update --pull    # Fast-forward a clean checkout from your GitHub
meshall-update --local   # Rebuild your own local edits
```

`--check` validates the target without changing it. `--without-rdp-worker`
omits the optional protocol dependency for environments that do not use it.

## Windows MSI and GPO deployment

**Add device** also offers an enrollment-ready **Windows x64 (.msi)** for silent,
computer-assigned Group Policy deployment. It installs the native agent as an
automatic LocalSystem service, with the server/group settings included. Deployment
keys support up to 10,000 computers and 90 days for their first enrollment.

```powershell
msiexec.exe /i "\\fileserver\software\EndpointControlAgent-0.10.0-win-x64.msi" /qn /norestart
```

The Debian installer provides the local MSI build tools (`wixl` and `msitools`).
See [Windows GPO deployment](docs/WINDOWS-GPO.md) for UNC share permissions,
computer assignment, updates/repair/removal, existing-agent handling and the local
C# packaging commands.

After generating an MSI, **Add device → Windows agent MSI** retains an authenticated
**Download Windows x64 MSI** link across page reloads. The package lives in the
instance's private data directory and is served with no-store caching and enrollment
permission checks. Revoked, expired or exhausted deployment keys hide that download.

## EndpointControl branding and upgrade compatibility

EndpointControl is the application and repository name. Its monitor/control logo is
shared by the UI, favicon, Windows installers, native agent resources and approval
dialog. Existing deployment paths, the `MeshAllAgent` service/file identity, configuration
section, database filename, cookie identities and Data Protection purposes remain
compatible with prior installations. This preserves enrolled devices, account hashes,
authenticators and saved encrypted connection defaults through the rename. Technical
project/solution paths below refer to those compatible internal identifiers.

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

## Interface languages

The top bar includes an **English / Français** selector, also available on the
sign-in page. The selection is stored in a language cookie and applies across
navigation and later sessions. Without a saved choice, supported browser language
preferences are used, with English as the fallback.

Pages, forms, tables, confirmation dialogs, application statuses and validation
messages use .NET `.resx` resources. Dates, numbers and capacity units follow the
selected culture. Device/user names, script contents, filesystem paths and raw
command output retain their original values.

## User privacy

Groups expose **Ask the user before showing their screen**, notification, and a
connection toolbar. New groups use the original unattended default; prompting
is optional and enabled explicitly. The retained agent core
has EndpointControl modifications for fresh per-session approval, no remembered five-
minute approval when strict mode applies, and denial on timeout. RDP/VNC/AMT
screen transports request agent approval before opening when the group policy
requires it.

Required screen approval is requested in the active user's Windows session, not
the background service or an empty console logon session. The native Yes/No window
does not depend on administrator avatars. Approval is bound to one request and
desktop session; no desktop tunnel, frames or input is opened before it is granted.
No response after 30 seconds, closing the dialog, denial or an unavailable prompt
stops access without automatic retry. A new Connect needs a fresh decision.

## Account security and roles

**Security** in each user's profile enables optional authenticator-app 2FA.
The server verifies TOTP codes, issues single-use recovery codes, protects stored
authenticator keys with ASP.NET Core Data Protection, and rejects reused codes.
A valid password does not create a full sign-in session before the second factor.

In **App settings**, an administrator may enable **Require two-factor authentication
for all users**. This applies at the **next sign-in**: users without 2FA are limited
to enrollment until setup is complete. Existing signed-in sessions continue.

**Roles** provides custom capability sets, and **Users** assigns those roles.
Capabilities cover inventory/editing/enrollment, desktop viewing and control,
terminals, commands, files, network tools, power, scripts, audit, users and settings.
Permissions are checked on the C# server as well as in Blazor. Role changes revoke
affected sessions. The built-in Administrator retains full access, and at least
one active Administrator must remain.

For lost-authenticator recovery when no recovery code remains, a local server
administrator can run the server binary with the instance's configuration:

```bash
MESHALL_CONNECTION_FILE=/etc/meshall/appsettings.json \
  /opt/meshall/current/MeshAll.Server --reset-two-factor USERNAME
```

This resets only that account's factor and revokes its sessions. If instance
enforcement is enabled, its next sign-in requires enrollment again. Preserve the
instance data directory and its `keys` directory together when backing up.

## Device inventory

The native agent core reports OS/architecture, logical CPU cores/model, memory,
uptime, local interfaces/MACs and disks, with automatic refresh every 15 seconds
and a **Refresh inventory** action. The device's connection address is recorded
separately from its primary interface address. Missing data is displayed as
unreported; heartbeat messages no longer replace collected inventory with zeros.
Agent-core changes are delivered automatically by the C# server without requiring
a native executable rebuild for each inventory improvement.

## Device dashboard and resource statistics

The **Dashboard** summarizes every connected, visible, unblocked device:

- Connected/offline/blocked counts and a database count of active sessions.
- Average CPU, memory and storage usage percentages, with each reporting device
  counted equally. Reported RAM/storage capacity is secondary context.
- Nearly full disks (90% used), critical storage (97% used), high CPU (85%) and
  high memory usage (90%), with device counts and actionable filters.
- A searchable, sortable connected-device table and CPU, memory and disk
  statistics on each device's Overview tab.

The UI refreshes every five seconds. CPU, memory and disk samples have separate
server-receipt timestamps. Readings older than two minutes are excluded from device
averages; unavailable samples appear as missing rather than zero usage.

Sessions, audit events, devices, users, roles, scripts, enrollment keys and groups
use database pagination: **50 rows maximum**, with 10/25/50-row controls. Searches
cover the whole matching database history. Active-session counters use `COUNT(*)`,
and explicit audit exports stream rows. File/process/interface/disk tables page
their requested inventory snapshots instead of rendering every item.

Reusable Blazor components live in `Components/Common`. See
[development conventions](docs/DEVELOPMENT.md) for adding a view without copying
table markup or loading an entire history.

## Desktop viewing and device removal

The device's **Desktop** tab connects inside the device page. Its image fits the
available viewport space while preserving aspect ratio, including when the browser
or remote resolution changes. **Fullscreen** provides a larger view. The standalone
viewer remains available under **Remote tools → Open in new tab**. Inline tools
share the same viewer component; there is no nested iframe or scrollable desktop.
Fullscreen controls are available before connecting and after disconnecting, with
an explicit **Exit fullscreen** action. Disconnecting does not trap the view in
fullscreen; leaving the viewer exits its fullscreen workspace automatically.

Native desktop transport interruptions (including Windows sign-in/desktop changes)
recover in the same viewer with backoff and a retained last frame. Keyboard/mouse
control starts automatically when the role allows it and a real frame is available;
an explicitly selected view-only mode survives reconnects. Local sharing-bar close,
declined consent and revoked access stop recovery. The Windows sharing-bar helper
is recreated after a session change rather than treating its exit as a user stop.

**Remove device** removes a device from the website without revoking its agent's
registration. Heartbeats do not bring a removed device back; its next authenticated
agent connection does. Its group policy and identity are retained, and an existing
access block remains enforced. This uses the `devices.remove` capability, granted
to the built-in Administrator and configurable for custom roles.

## Remote tools and typed scripts

Save personal RDP defaults in **Security → Your RDP credentials**, alongside
your two-factor settings.
Username, optional domain and a masked password prefill inline and standalone RDP
connections. Defaults belong only to the signed-in user; passwords use user-bound
ASP.NET Core Data Protection encryption. Save changes or clear the defaults at any
time. Saving defaults requires the RDP capability and does not change account login
credentials or other users' connection preferences.

**Agent desktop** is the built-in, setup-free screen transport. **VNC** connects to
an already installed VNC service through the agent, using the device's remembered
port (default 5900). It opens without an up-front username/password form and asks
for only the credentials requested by that VNC service. Advanced connection settings
remain available; device editors can remember a nonstandard VNC port.

The agent authenticates the management tunnel; it does not silently install a VNC
server or bypass its password.

Saved scripts have an explicit type: **PowerShell (.ps1)**, **Command Prompt
(.cmd)**, **Bash (.sh)** or **Shell (.sh)**. Bash runs explicitly under `/bin/bash`;
the separate Shell type preserves POSIX `/bin/sh` execution. Selecting a saved script sets the device's execution
shell automatically. Explicitly typed scripts run as real temporary files, including
batch labels/`exit /b` and PowerShell `param(...)`, with output and process exit status
returned to EndpointControl. Temporary files are cleaned up afterward. Existing scripts
retain platform-default execution unless their filename already identifies a type.

Native desktop, terminal and PowerShell cards appear first under **Remote tools**;
network services follow. Cards use an icon followed by their title and description.
**Logs** groups paged session history and audit history into permission-aware tabs.

## Parity and verification

**100% functionality is the acceptance target.** Retaining working low-level
technology is intentional; it does not automatically prove complete parity of
the new C# orchestration/UI. `docs/COMPATIBILITY.md` tracks implementation and
end-to-end verification per workflow. This development version must not be
represented as fully verified until that matrix is complete.

## License

EndpointControl's original C#, Razor, adapters and scripts are **MIT**. Third-party
agent/viewer code, xterm.js, noVNC, Guacamole, .NET and other dependencies retain
their own licenses. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.
