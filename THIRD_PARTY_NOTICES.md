# Third-party notices

EndpointControl's original C#, Razor, browser adapters, scripts, and documentation are
MIT-licensed. This does not replace the licenses of its dependencies.

| Component | License | Use |
|---|---|---|
| .NET / ASP.NET Core / Blazor | MIT and included runtime notices | Application framework |
| Microsoft.Data.Sqlite / SQLitePCLRaw | MIT / Apache-2.0 and bundled SQLite notices | Persistence |
| QRCoder | MIT | Locally generated authenticator enrollment QR codes |
| System.Drawing.Common | MIT and framework notices | Windows GDI+ screen capture |
| Tailwind CSS | MIT | Compiled UI styling |
| Native MeshAgent-derived C/C++ technology | Apache-2.0 and component notices | Native PC agent maintained under `components/agent-native` |
| Agent-side core/modules and desktop/AMT viewers | Apache-2.0 and retained notices | Native remote access technology maintained in this repository |
| xterm.js | MIT | Terminal rendering when enabled |
| noVNC | MPL-2.0, with component-specific notices | Standards-based VNC rendering when enabled |
| Apache Guacamole / FreeRDP | Apache-2.0 and their dependency notices | Optional standard RDP/SSH protocol worker |

Third-party browser assets retain their license files beside the generated
assets. NuGet/npm package licenses remain applicable to those packages. Native
protocol workers are dependencies, not EndpointControl-owned source.

The product does not include or launch the original MeshCentral JavaScript
server/orchestrator. Native agent/viewer technology is intentionally retained,
attributed, and maintained in this repository. Baseline binary provenance is in
`components/agent-binaries/provenance.json`; retained source keeps its own notices.
Single-file agent downloads embed the native Apache-2.0 license, EndpointControl MIT
license and these notices ahead of the native embedded-policy trailer. The same
notices are available from the instance's `/licenses/agent` endpoint.
