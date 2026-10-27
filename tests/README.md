# Live native-agent acceptance

Server-side security and protocol unit/integration checks run without test VMs:

```bash
dotnet test tests/MeshAll.Tests/MeshAll.Tests.csproj
```

They exercise TOTP interoperability, full-session gating, enforcement at the next
login, recovery/replay protection, CSRF, capability policies, role revocation,
inventory merging and the native executable policy trailer. GitHub CI runs them.

Desktop/sharing-bar lifecycle checks also run in Node, including immediate input
and capture detachment, local-stop acknowledgement, bounded cleanup and native
connection-count preservation:

```bash
node --test tests/desktop-session.test.mjs tests/remote-session.test.mjs tests/screen-consent.test.cjs tests/consent-dialog.test.cjs tests/notifybar.test.cjs tests/inventory.test.cjs
```

`tests/remote_workflows.py --base-url URL --auth-state /private/state.json` runs on
a disposable instance. It drives the actual native viewer against a simulated KVM
transport, forces a disconnect, verifies automatic recovery/input and local-user
stop, tests `.cmd`/`.ps1` library-to-device routing, and negotiates VNC None/password
authentication against isolated loopback fixtures.

## Device dashboard and paged tables

On a disposable instance with no other agents connected:

```bash
.venv/bin/python tests/device_dashboard.py \
  --base-url https://your-disposable-instance:5188 \
  --auth-state /private/administrator-session.json \
  --database /private/disposable-data/meshall.db \
  --screenshots /private/device-screenshots
```

This creates controlled resource reporters and temporary database fixtures. It
checks equal-device CPU/memory/storage averages, offline exclusion, capacity alerts, per-device
readings, 263-session history pagination/search, the other paged lists, mobile
layout and dark mode. Its own fixtures are removed afterward. Database tests also
place malformed data outside the requested page to prove those rows are never
materialized.

## English/French localization

```bash
.venv/bin/python tests/localization.py \
  --base-url https://your-disposable-instance:5188 \
  --auth-state /private/administrator-session.json \
  --screenshots /private/localization-screenshots
```

This checks concise login copy, language persistence on anonymous and static
pages, both languages across pages/editors, translated dialogs and connection
statuses, preserved script identifiers/output, and French desktop/tablet/mobile
layouts. Unit tests cover resource/placeholder parity, culture-aware formatting,
antiforgery, local redirects and language selection during required 2FA setup.

## Layout, navigation and removal regressions

On a **disposable instance** with an authenticated administrator browser state:

```bash
.venv/bin/python tests/ui_regressions.py \
  --base-url https://your-disposable-instance:5188 \
  --auth-state /private/administrator-session.json --allow-self-signed
```

This enrolls a uniquely named simulated agent and uses generated landscape and
portrait frames. It checks inline and standalone auto-connect, tab return, no nested
desktop iframe or viewer scrollbars, window resizing, fullscreen, pointer scaling, theme/navigation across Settings
and Security, device removal, heartbeat behavior and return on reconnect. It
removes the test device from the list and revokes its enrollment key afterward.

`tests/fullscreen_workflows.py --base-url URL --auth-state /private/state.json`
checks inline/standalone Desktop and Terminal plus PowerShell in English/French.
It enters/exits fullscreen before connection, disconnects while fullscreen,
reconnects, forces a terminal socket close, simulates browser fullscreen refusal,
checks recovery after an asset-load failure, verifies browser-initiated exit and
navigation cleanup, and reviews 1440/768/390px layouts. Optional `--screenshots DIR`
captures disconnected fullscreen views. Its terminal fixtures intercept only the
disposable browser's sockets, and enrollment/device fixtures are cleaned up.

## Personal RDP defaults

```bash
.venv/bin/python tests/connection_preferences.py \
  --base-url https://your-disposable-instance:5188 \
  --auth-state /private/administrator-session.json \
  --screenshots /private/connection-screenshots
```

Use a disposable account with no existing RDP defaults. The test saves a random
fixture password, checks masked save/reload/update/clear and inline/standalone
prefill, English/French account Security placement and language-arrow spacing, and rejects
plaintext in prerendered HTML/audit exports. It removes its credentials, device
and enrollment-key fixtures. Server tests cover ciphertext, cross-user decryption
failure, account/capability revocation and required-2FA gating.

`tests/management_workflows.py --base-url URL --auth-state /private/state.json`
checks native-first horizontal tool cards, Bash save/selection, shared dropdown
spacing/full-width behavior, permission-aware Logs navigation and RDP defaults
beside two-factor controls. Its fixtures are disposable and cleaned up.

Security boundary tests include cookie replay after logout, bounded legacy-cookie
migration, delegated privilege-escalation rejection, controller-device isolation,
cross-origin interactive socket rejection and revocation during remote setup.
`python3 tests/source_packaging.py` rejects local state/secrets in source archives.
`python3 -O tests/native_artifact_import.py` checks hash-pinned imports offline,
including rejection before an existing artifact can be overwritten. The optimized
Python invocation ensures artifact validation cannot disappear with assertions.
See `docs/SECURITY-REVIEW.md` for the scope and validation limits.

## Windows MSI packaging

After a Release build, on Linux with `wixl` and `msitools` installed:

```bash
python3 tests/msi_packaging.py
```

This builds an actual self-contained native-agent MSI, inspects its compiled service,
64-bit registry, upgrade/rollback and launch-condition tables, and extracts the
embedded native executable/notices. It checks that package creation consumes no
enrollment uses, includes no generated device identity, and cannot overwrite an
existing output. Source-packaging tests reject generated MSI/MST/CAB files.
Server tests check MSI-download authorization, revoked enrollment keys, architecture
and payload validation, stable upgrade identities and bounded bulk-deployment keys.
See `docs/WINDOWS-GPO.md` for a domain computer-startup pilot.

`tests/msi_workflows.py --base-url URL --auth-state /private/state.json` checks
the authenticated download, anonymous rejection, no-store response, unchanged key
uses, and English/French GPO instructions at desktop/tablet/mobile widths on a
disposable instance with the packaging tools installed.

## Live native-agent checks

These opt-in checks drive the actual Blazor UI, C# orchestrator, enrolled native
agent and target services. They require a disposable/test device and a workspace
account with remote-management access.

```bash
python3 -m venv .venv
.venv/bin/pip install -r tests/requirements.txt
.venv/bin/playwright install chromium
export MESHALL_USERNAME='your-test-user'
read -rsp 'MeshAll password: ' MESHALL_PASSWORD; export MESHALL_PASSWORD
.venv/bin/python tests/acceptance.py \
  --base-url https://your-meshall-host:5188 \
  --device YOUR_DEVICE_ID --platform windows \
  --checks commands processes files terminal desktop
```

Use `--allow-self-signed` only for a test server with a self-signed certificate.
Alternatively, `--auth-state /private/session.json` loads an existing Playwright
session. Do not put credentials, session state or screenshots in Git.
For a 2FA-enabled test account, use a private storage state from a completed
two-factor sign-in. The basic username/password automation does not bypass 2FA.

- Commands verify actual output and reported success, not only echoed input.
- Files create a uniquely named temporary folder, upload/download a 133 KB binary
  payload including every byte value, compare bytes, then delete both artifacts.
- Terminal expands/joins input before checking output to distinguish execution
  from the terminal echo.
- Desktop/RDP/VNC/SSH connect, verify nonblank **composited browser pixels**, and
  reconnect. Canvas dimensions alone do not catch hidden/behind-background layers.
- These display checks do not inject desktop keyboard/mouse input. Input acceptance
  needs a controlled application on the test PC.

For `--checks rdp`, `--checks vnc` or `--checks ssh`, set
`MESHALL_REMOTE_USERNAME` and `MESHALL_REMOTE_PASSWORD`. The target service must
already be configured. `--port` selects a nonstandard target port; `--direct`
tests agentless access, otherwise traffic uses the native agent's TCP tunnel.
RDP/SSH also require a host identity verified through the registered agent or
administrator configuration; an unknown offline identity fails closed.
Use separate invocations for services with different credentials or ports.

`--renderer src/MeshAll.Server/wwwroot/app.js` tests local renderer changes against
a running test deployment. `--screenshots /private/output` saves visual evidence.
The default run only checks commands, processes, files and terminal.

## Controlled Windows keyboard/mouse acceptance

Run `tests/windows-input-fixture.ps1` in the test user's interactive **console**
session. It prints a unique temporary output path and shows a text box with a
save button. In MeshAll's native desktop, confirm keyboard/mouse is already
enabled, click the text box, type a unique marker and click **Save verification
and close**.

Use the separate **Commands** tab to read the printed output path with
`Get-Content -LiteralPath 'THE_PRINTED_PATH'`. Check the exact marker, then remove
the result file and its uniquely named `MeshAll-Input-*` parent directory. This
checks real keyboard input and mouse actions independently of the viewer pixels.
An active RDP session and the console are distinct Windows sessions; ensure the
fixture is visible in the session the native viewer is displaying.

For Windows transition recovery, keep the same viewer open across lock/sign-in
and an agent-service restart. Confirm capture updates and control returns without
toggling the checkbox, then repeat the independent marker verification. If the
group enables a sharing bar, close its actual Windows button and verify the
viewer reports a local-user stop without reconnecting. A deliberate new
**Connect** must create a usable sharing session again.
