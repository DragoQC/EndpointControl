# MeshAll implementation record

## Final requested boundary

- C# orchestration/control plane; SQLite management state.
- Blazor Server and Tailwind management UI.
- Retain native C/C++ agent and working remote protocol/viewer technology in the
  appropriate languages, maintained in this repository with attribution.
- Do not launch the original MeshCentral Node.js server underneath the UI.
- Full functionality is the parity/acceptance target, tracked per workflow.

The initial API-adapter/retained-server approach and the subsequent all-C# agent
experiment were superseded by this boundary. The additional .NET agent source
is not the default native-agent replacement.

## Observations

The initial authorized host ran MeshCentral 1.3.0. REA 6.7.0 was used with a
disposable Chrome/CDP session. Discovery evidence:
`ev_92efc91197a2f6236128248e94dc7389c8c9ad61e4d9d41601b858bf602e0a55`.
Page observation:
`ev_ee1ef16c2381398c7574cbeca14e429847457f4431a27970f3d471fe6108a171`.
These attach-limited observations do not establish complete protocol parity.

Native component provenance is retained beside the components. The C# server
implements native authentication, enrollment, core delivery and relays directly.
This is an attributed native-technology reuse and C# orchestration rewrite;
traditional independently staffed legal clean-room separation is not claimed.

## Deployment target

The target is the disposable Debian VM at 192.168.120.112, hostname `meshcentral`.
A duplicate IP briefly routed to `radarr`; no installation changes were made on
that host. Deployment must verify the intended hostname/SSH identity first.

## License

Original MeshAll code: MIT. Retained native/viewer components and general
dependencies retain their own licenses. No third-party code is relabeled MIT.
