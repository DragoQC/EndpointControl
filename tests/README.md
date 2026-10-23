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
node --test tests/desktop-session.test.mjs tests/notifybar.test.cjs
```

`tests/remote_workflows.py --base-url URL --auth-state /private/state.json` runs on
a disposable instance. It drives the actual native viewer against a simulated KVM
transport, forces a disconnect, verifies automatic recovery/input and local-user
stop, tests `.cmd`/`.ps1` library-to-device routing, and negotiates VNC None/password
authentication against isolated loopback fixtures.

## Layout, navigation and removal regressions

On a **disposable instance** with an authenticated administrator browser state:

```bash
.venv/bin/python tests/ui_regressions.py \
  --base-url https://your-disposable-instance:5188 \
  --auth-state /private/administrator-session.json --allow-self-signed
```

This enrolls a uniquely named simulated agent and uses generated landscape and
portrait frames. It checks auto-connect, no nested desktop iframe or scrollbars,
window resizing, fullscreen, pointer scaling, theme/navigation across Settings
and Security, device removal, heartbeat behavior and return on reconnect. It
removes the test device from the list and revokes its enrollment key afterward.

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
