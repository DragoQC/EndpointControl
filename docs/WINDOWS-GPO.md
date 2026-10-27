# Windows agent deployment with Group Policy

EndpointControl can build a self-contained **Windows x64 MSI** for computer-assigned GPO
deployment. The MSI contains the native C/C++ agent with its server, group and
enrollment settings already embedded. Endpoints do not need .NET, an accompanying
configuration file, a transform, or an interactive installation step.

## Generate the package

1. Open **Devices → Add device** with an account that has enrollment permission.
2. Choose the destination device group and create a dedicated enrollment key.
3. Set enough uses and time for the computers' **first successful connection**.
   Keys support up to 10,000 devices and 2,160 hours (90 days); defaults remain one
   device and 24 hours. Building/downloading an MSI does not consume a use.
4. Download **Windows x64 (.msi)**. The filename includes the package version,
   for example `EndpointControlAgent-0.10.0-win-x64.msi`.

After the first MSI generation, the **Windows agent MSI** card on **Add device**
provides a persistent **Download Windows x64 MSI** link at `/api/agents/windows-msi`.
The package remains in private instance storage across page reloads. Only users
with enrollment permission can download it, and an expired/revoked/exhausted key
removes it from the available downloads.

The server installer installs `wixl` and `msitools`. For an existing development or
locally managed Debian installation, install them with:

```bash
apt-get install wixl msitools
```

Packages are compiled on the EndpointControl server in a private temporary directory,
which is removed afterward. Enrollment material is not sent to an external build
service. MSI downloads require a current authenticated session and enrollment
permission; the download response is not cached.

## Computer-assigned Group Policy

1. Copy the downloaded MSI to an immutable, versioned network-share path, such as:
   `\\fileserver\software\EndpointControl\0.10.0\EndpointControlAgent-0.10.0-win-x64.msi`.
2. Give the target computer accounts (commonly **Domain Computers**) read access
   in both the share and NTFS permissions. Give software-deployment administrators
   write access. Computer startup runs as the computer, not as the signed-in user.
3. In **Group Policy Management**, create or edit a GPO linked to the target
   computers' organizational unit.
4. Open **Computer Configuration → Policies → Software Settings → Software
   installation**, select **New → Package**, and enter the full **UNC path**.
   Select **Assigned**. Do not browse to a local drive or mapped drive letter.
5. Scope the GPO to Windows x64 computers. If startup occurs before the share is
   reachable, configure the applicable Windows policy to wait for the network at
   computer startup and logon.
6. Apply the policy and restart a pilot computer. Computer-assigned software is
   installed during startup; `gpupdate /force` alone may request a restart.
7. Verify the compatible `MeshAllAgent` service identity and the new device record in EndpointControl before
   expanding the assignment.

Keep an assigned MSI available at its original share path for Windows Installer
maintenance. Do not overwrite an assigned package in place; publish subsequent
packages under new versioned paths.

## Silent installation and verification

The same package supports administrative deployment tools and startup scripts:

```powershell
msiexec.exe /i "\\fileserver\software\EndpointControl\0.10.0\EndpointControlAgent-0.10.0-win-x64.msi" /qn /norestart /L*v "C:\Windows\Temp\EndpointControl-Agent-install.log"
```

Windows Installer success is `0`; `3010` means success with a restart required.
The package does not force a restart. Check the service with:

```powershell
Get-Service -Name MeshAllAgent
Get-CimInstance Win32_Service -Filter "Name='MeshAllAgent'" |
    Select-Object Name, State, StartMode, StartName, PathName
```

The service installs per-machine, starts automatically as **LocalSystem**, and runs
from `C:\Program Files\MeshAll\MeshAllAgent\MeshAllAgent.exe`. Installation does not
need an available controller: the agent connects/retries normally afterward. Its
first enrollment must succeed before the key expires or runs out of uses. An
expired/revoked key does not disconnect devices already enrolled with their own
certificate identities.

The selected group's screen-approval, notification and sharing-bar policies apply
normally. The MSI does not disable them or make a new consent decision.

## Updates, repair and removal

- Generate the next package from the same EndpointControl instance and publish it at a new
  share path. Add it to Software installation and use the package's **Upgrades**
  settings to supersede the previous assignment.
- The MSI upgrade family and component identity are stable. A changed native
  payload/enrollment key or package version gets a new ProductCode. Same-version
  replacement is supported for renewed deployment keys; newer-version downgrade
  protection still applies.
- Upgrade removes the previous MSI inside the installation transaction, stops the
  service before replacing application files, and starts the new service. A service
  installation failure is fatal to the transaction. Rollback can restore the prior
  MSI. The native database/certificate identity is not an MSI payload and is not
  removed during upgrades.
- Renewing an enrollment key does not move an already enrolled device into a new
  group. Manage existing device/group membership in EndpointControl.
- For repair, use the originally assigned package:

  ```powershell
  msiexec.exe /fa "\\fileserver\software\EndpointControl\0.10.0\EndpointControlAgent-0.10.0-win-x64.msi" /qn /norestart
  ```

- Remove a GPO assignment through Software installation, or uninstall its installed
  ProductCode:

  ```powershell
  msiexec.exe /x "{INSTALLED-PRODUCT-CODE}" /qn /norestart
  ```

  Uninstall stops/removes the service and MSI-owned executable/notices. Native
  runtime state is retained for deliberate reinstallation; there is no wildcard
  database/identity purge. The installation directory is removed only when empty.

The package detects an agent installed by the EXE/native installer and stops before
replacing it. It also detects an MSI bound to another EndpointControl instance. Migrate
existing installations separately: preserve the original agent identity/state and
confirm the destination instance and service path before changing ownership to MSI.
Do not use the native EXE's Install/Update/Uninstall buttons to maintain an
MSI-owned installation; use Windows Installer/GPO for its application files.

## Local package creation

The C# server also exposes a local administrative packaging command. Run it as the
instance's service account, with its usual configuration and an existing output
directory writable by that account:

```bash
runuser -u meshall -- env MESHALL_CONNECTION_FILE=/etc/meshall/appsettings.json \
  /opt/meshall/current/MeshAll.Server --agent-msi ENROLLMENT_KEY --output /private/output/EndpointControlAgent.msi
```

To package a Windows x64 EXE already downloaded from **Add device**, including one
from another instance:

```bash
MeshAll.Server --agent-msi-from /private/input/EndpointControlAgent.exe --output /private/output/EndpointControlAgent.msi
```

The EXE must have a valid native embedded policy and compatible service/file names.
Offline `--agent-msi-from` packaging does not open/create controller state. Local
packaging commands do not mark running remote sessions as interrupted; that recovery
step runs only when the HTTP server itself starts.
The output must not already exist; the command does not overwrite assigned MSIs.
On Unix the created package is owner-readable/writable until the administrator
copies it to the managed distribution share. Keep generated `.msi`, `.mst`, `.cab`
and enrollment-ready executables out of source archives and Git.

Custom tool locations can be set with `MeshAll:MsiCompilerPath` and
`MeshAll:MsiDatabaseToolPath`. The default tools are `/usr/bin/wixl` and
`/usr/bin/msibuild`. Debian's wixl 0.106 does not implement every authoring attribute;
the builder explicitly normalizes transactional upgrade scheduling and the vital
service-install flag with msibuild. Integration tests inspect the compiled tables
and extract the real embedded payload.

## Scope and acceptance

This package targets Windows x64. ARM64/macOS/Linux executable downloads retain
their existing installation workflow. Package-generation tests verify architecture,
service registration/control, upgrade scheduling, downgrade/ownership conditions,
embedded payload/notices, unchanged enrollment-use counts, and source exclusions.
An actual Active Directory computer-startup rollout needs a domain/share and its
own pilot acceptance; package-generation tests do not simulate that environment.
An isolated Wine Windows Installer smoke test also started the native service and
verified application removal/state retention after service shutdown. This is
supplemental coverage, not native Windows or Active Directory pilot acceptance.

Publisher signing and Windows Defender reputation work remain tracked separately
in [development conventions](DEVELOPMENT.md).
