# Development conventions

Windows Defender false-positive/reputation work is deferred. A future distribution
pass should validate Authenticode signing and timestamping with the personalized
agent format, use a consistent publisher/installer, and submit false positives to
Microsoft. Signing/reputation does not guarantee that security tools will not flag
remote-management functionality. This pass does not change Defender configuration.

## Code readability

Use explicit C# types for local declarations, `using`, `foreach` and `out`
variables. `.editorconfig` sets that preference for C# and Razor. Keep statements
and control-flow blocks readable, with descriptive names for domain values.
Native-agent JavaScript deliberately uses ES5 `var`/`function` syntax because it
executes inside the retained native runtime. Retained third-party source keeps
its upstream structure and attribution.

```bash
dotnet format whitespace MeshAll.slnx
dotnet format style MeshAll.slnx --diagnostics IDE0008 IDE0011 --severity info
npm --prefix src/MeshAll.Server run format
```

The UI formatter covers first-party JavaScript, CSS, EndpointControl native modules and
browser lifecycle tests. Rebuild generated assets with
`npm --prefix src/MeshAll.Server run build`.

The same scope is checked without rewriting files by
`npm --prefix src/MeshAll.Server run format:check`. CI also checks C# whitespace,
unused imports, explicit local types and braces. Keep formatter changes separate
from changes to protocol or application behavior when reviewing a refactor.

## Code organization

- `Program.cs` is the composition root. `ServerHosting.cs` registers services and
  authentication; its `.Middleware.cs` partial owns pipeline ordering and its
  `.Endpoints.cs` partial owns HTTP routes and their capability/rate-limit metadata.
- `Store.Users.cs`, `Store.Enrollment.cs` and `Store.Scripts.cs` own their domain
  queries and validation. Existing security, roles, connection defaults, paging and
  telemetry partials retain their own boundaries. Use the parameterized helpers in
  `Store.Database.cs`, including commands participating in an explicit transaction.
- `AgentGateway.Desktop.cs` owns managed/native desktop relays, including approval
  preflight and cancellation. `NativeAgentPeer.Files.cs` owns the native file
  protocol; the peer's main file owns its connection, inventory and commands.
- `AgentOperations.Commands.cs` owns managed-agent script execution and output;
  `StreamSession.cs` owns managed terminal/TCP streams. `Wire.cs` contains shared
  framing/serialization and limits; `Contracts.cs` contains transport records.
- Dashboard and device markup live in `Home.razor` and `Device.razor`; their
  `.razor.cs` files own filtering, authorization-aware actions and lifecycle code.

File boundaries do not change authorization, message shapes, approval timeouts,
database migrations, encryption purposes or session ownership. Preserve those
contracts when extracting helpers. In native JavaScript, keep the standalone
approval-child script explicit and small: the native launcher has a bounded command
line and Duktape does not retain source for `Function.toString()`. Display text and
branding travel over its private stdin pipe.

## Reusable UI components

`src/MeshAll.Server/Components/Common/` contains:

| Component | Purpose |
|---|---|
| `PageHeader` | Title, breadcrumb, description and actions |
| `DataTable<TItem>` | Typed templates, stable row keys and empty states |
| `PagedTable<TItem>` | Database-page table, toolbar and shared pagination |
| `Pagination` | Counts, page navigation and 10/25/50-row selection |
| `SearchField` | Debounced search input |
| `StatCard` | Device summary metrics |
| `ResourceMeter` | Accessible percentage, thresholds and missing-data state |
| `DeviceResourceStats` | Per-device CPU, memory and disk capacity |
| `CommandPanel`, `ProcessPanel`, `FileBrowser` | Device operational views |
| `RemoteViewer` | Shared inline/standalone desktop, terminal and network viewers |
| `RdpCredentialsSettings` | Current user's protected connection defaults editor |
| `SelectField<TValue>` | Shared native dropdown, arrow spacing, typed value and optional full width |
| `SessionLogs`, `AuditLogs` | Separate capability-checked tables under the Logs tabs |

String parameters containing C# values need Razor expressions (`Value="@Search"`).
Typed parameters such as `Result="Rows"` are C# expressions.

Use `SelectField` for every dropdown. The default fits its option content; set
`Expand="true"` for full-width form controls. Use `@bind-Value` for typed binding,
or `Value`/`Changed` for an existing change handler. Native `name`, `aria-label`,
disabled and data attributes pass through to the actual select element. Keep
chevron/padding rules in the shared component styles, not copied into pages.

```razor
<SelectField TValue="string" @bind-Value="shell" Expand="true" aria-label="Execution shell">
    <option value="bash">Bash (.sh)</option>
    <option value=".sh">Shell (.sh)</option>
</SelectField>
```

Derive database-backed pages from `PagedPageBase<TItem>` and implement
`ReadPage(PageRequest)`. Connect its page/search callbacks to the shared components:

```razor
@inherits PagedPageBase<SessionInfo>

<PagedTable Result="Rows" Label="Sessions" ItemKey="session => session.Id"
            PageChanged="ChangePageAsync" PageSizeChanged="ChangePageSizeAsync">
    <Header><th>Device</th><th>Operation</th></Header>
    <RowTemplate Context="session">
        <td>@session.DeviceName</td><td>@session.Kind</td>
    </RowTemplate>
</PagedTable>

@code
{
    protected override PageResult<SessionInfo> ReadPage(PageRequest request) =>
        Store.SessionPage(request, Search);
}
```

Actual pages also need authorization attributes and the correct `ViewPermission`.
Server-side actions/transports enforce their capability independently.

`Pages/Remote.razor` is a route wrapper; embed `RemoteViewer` directly in device
tabs rather than using an iframe. Key it by device/tool so replacing a view disposes
its session, layout observer and input handlers. Inline height follows available
viewport space; the canvas scales proportionally and fullscreen uses the same
surface. JavaScript cleanup is owner-scoped so an old component cannot close a new
session. Deliberate disconnect/local stop must not auto-reconnect.

Viewer markup lives in `RemoteViewer.razor`; authorization and component lifecycle
live in its `.razor.cs` code-behind. `RemoteFullscreenButton` is independent of
connection state. Its localized labels and pressed state reflect the browser's
actual fullscreen element, including browser-initiated exit.

`wwwroot/app.js` owns only synchronous theme/language/fullscreen controls and a
small lazy-loading interop facade. `wwwroot/remote/` contains the viewer controller,
workspace layout/fullscreen lifetime, connection lifetime, asset helpers and separate
native desktop, managed desktop, terminal, VNC and Guacamole transports. Keep browser
fullscreen calls directly in the click handler to retain user activation.

Register sockets, observers and input cleanup with `RemoteSession`. Cleanup is
idempotent, continues after a failed release, and rejects stale notifications from
old connections. Terminal/network closure ends that session; native reconnects stay
under `DesktopSession` control. Control, credentials and CAD commands include the
owner ID. Guacamole input listeners belong to its disposable display element.
Disconnection releases transport resources, while workspace controls remain mounted
until the view is disposed. Do not couple fullscreen to a connection's running flag.

`Store.Connections.cs` owns per-user RDP defaults. Require current RDP permission
and account version for reads/writes. Protect passwords with a separate Data
Protection purpose including the user's ID. Only decrypt during the authenticated
interactive editor/viewer lifecycle, never prerender. Keep secrets out of JSON DTOs,
audit details, exception text, browser storage and URLs. Connection option objects
are sent only through the authenticated connection flow.

The editor is an interactive island in the static account Security page, alongside
the two-factor forms. Enrollment-only sessions do not render it. Server Settings
contains only instance-wide configuration. Its UI label is **App settings**
(**Paramètres de l’application** in French). `/logs` owns the session/audit tabs;
the parent requires either capability and each table independently requires its own.

Remote user/role actions must use `Store.Manage*` methods, which check current
permissions and delegation limits. Low-level bootstrap/fixture methods are not
remote-action entry points. `Store.WebSessions.cs` revokes individual sign-ins;
`WorkspaceAuthenticationStateProvider` and relay validation recheck live sessions.
Controller-device access is a separate capability, including file and network
entry points. Review [security boundaries](SECURITY-REVIEW.md) before adding routes.

Local administrator commands live in `ServerCommandLine`; authentication handlers
are split into sign-in/setup and second-factor files. `Store.Database.cs` contains
the shared parameterized SQLite helpers; feature-specific queries belong in the
corresponding Store partial rather than the bootstrap file.

## Database paging

`Services/Paging.cs` bounds requests to 50 rows. `Store.Paging.cs` runs a count and
the requested `LIMIT` / `OFFSET` query in one SQLite read snapshot. Ordering has a
stable ID tiebreaker. Put filters in SQL before pagination: loading a list and then
calling `Take(50)` still materializes the full history.

Use aggregate queries for counts. Dashboard session totals do not read session
records; role/group cards count members without loading user/device inventories.
Audit downloads use `ExportAudit`, an asynchronous row stream.

Agent file/process/interface/disk lists are requested snapshots. Use
`PageResult<T>.FromSnapshot` for their controls; changing a snapshot page must not
start another remote command.

## Resource samples and device aggregates

The native collector samples every 15 seconds. Windows uses WMI/memory/PDH;
Linux uses `/proc` counters and filesystem data. CPU needs a counter delta, so
the first Linux sample is unknown. Timeouts and cancellation prevent old
in-flight collections from publishing after a restart.

`NativeInventory` validates reports and advances `CpuReportedAt`,
`MemoryReportedAt` and `DisksReportedAt` only for corresponding valid samples.
General inventory updates and heartbeats do not refresh these ages. Managed
reports also use server receipt time.

`Store.Resources.cs` projects only resource fields of visible, unblocked,
connected devices. It does not load full network inventories or session rows.
`DeviceSummarySnapshot` computes the arithmetic mean of each reporting device's usage
percentage for CPU, memory and storage. Every device has equal influence. Core
counts do not affect the CPU average. Each device's storage percentage uses
the used/total bytes of its valid local volumes; disk alerts inspect every volume.

Reported RAM/storage capacities remain available as secondary totals. They do
not weight the headline percentages. Keep individual full-disk and overloaded
device counts in **Where to focus**, even when the overall average looks healthy.

Each aggregate includes valid samples at most two minutes old. Unknown values
never become zero; zero itself is a valid idle sample. Capacity alerts count a
device once even when multiple disks are nearly full.

## Verification

```bash
dotnet test tests/MeshAll.Tests/MeshAll.Tests.csproj
node --test tests/desktop-session.test.mjs tests/remote-session.test.mjs tests/screen-consent.test.cjs tests/consent-dialog.test.cjs tests/notifybar.test.cjs tests/inventory.test.cjs
python3 tests/source_packaging.py
python3 -O tests/native_artifact_import.py
dotnet format whitespace MeshAll.slnx --verify-no-changes --no-restore
dotnet format style MeshAll.slnx --verify-no-changes --no-restore --diagnostics IDE0005 IDE0008 IDE0011 --severity info
npm --prefix src/MeshAll.Server run format:check
```

On Linux, install `wixl` and `msitools` and run `python3 tests/msi_packaging.py`
after the Release build for real MSI compilation, table/sequence inspection and
native payload extraction. `NativeMsiPackage` owns declarative authoring/identities;
`NativeMsiBuilder` owns bounded local tool execution and private temporary files.
The MSI route requires enrollment permission. See [Windows deployment](WINDOWS-GPO.md)
for lifecycle and runtime-state ownership.

Native artifact imports are safe to import as a Python module; only the CLI entry
point downloads/writes files. Pinned hash and size validation uses explicit errors,
so it remains active under `python3 -O`. Its offline tests check that an invalid
download cannot replace an existing artifact.

`tests/device_dashboard.py` drives a disposable instance with controlled telemetry
and more than 250 history rows. It checks per-device averages, individual values, shared
pagination, older-history search and desktop/mobile themes. See `tests/README.md`
for browser and live-agent checks.

## Spacing

`Styles/app.css` defines shared `--space-*`, `--page-gutter`, `--panel-inset` and
`--section-gap` tokens. Panel headers, table edges, toolbars and paging controls
share the same horizontal inset. Grids own their gaps; their child panels do not
also add bottom margins. Editor panels and their forms own vertical spacing, so
avoid additional `mt-*` / `mb-*` utility margins on their children.

Use `form-grid three-column` for three related metadata fields and the named
permission/privacy fieldsets for checkbox groups. Review desktop, tablet and
mobile screenshots when changing these shared rules, including operational and
static account pages.

## User-facing copy and localization

Use direct headings and short operational instructions. Avoid slogans,
decorative subtitles and repeated explanations. Keep instructions that explain
security, permissions, required services, transfer limits or destructive actions.

English and French application strings live in
`Resources/Localization/UiText.resx` and `UiText.fr.resx`. Add corresponding keys
to both files and preserve every format placeholder. English source phrases are
the keys. Do not create keys differing only by case: the resource compiler treats
those names as duplicates.

Razor imports the `UiText` service as `Ui`:

```razor
<button>@Ui["Save changes"]</button>
<span>@Ui["Role for {0}", user.Name]</span>
<span>@Ui.Plural(count, "{0} device", "{0} devices")</span>
```

Shared heading, empty-state, search, table, stat-card and meter components
translate their label/text parameters. Localize direct child content, dynamic
labels, accessible names and confirmation text at their call sites. Keep option
values and protocol identifiers canonical even when the visible option is
translated. Use `DisplayFormat` for culture-aware numbers, units and relative
times; CSS/wire numbers remain invariant.

ASP.NET Core request localization supports `en` and `fr`. `LanguageSwitcher`
submits an antiforgery-protected form to `/culture`, stores the culture cookie
and reloads a validated local return URL. A full reload establishes the culture
for a fresh Blazor circuit and static account pages alike.

Stored events and errors keep their canonical source values. `Ui.Message`
translates known application messages when displaying them. Custom names,
paths, script contents and raw remote output are data and must not be translated.
