# Retained working technology

These components intentionally remain in their native languages. EndpointControl's
orchestration, storage and management UI are C#/SQLite/Blazor; no original
JavaScript server/orchestrator is imported here.

- `agent-native/`: native C/C++ agent source from Ylianst/MeshAgent, baseline
  commit `26846b1c7f0841d71938f6bebc96eef5a947d217`, with EndpointControl display/service
  naming and consent-module changes. Original licenses and notices remain.
- `agent-core/`: retained on-PC agent runtime/core and modules from the 1.3.0
  baseline (`a0cb50275e8a64f85ca199f687eed5d6bd91fcc9`), with explicit per-session
  EndpointControl consent changes. These scripts run inside the native agent.
- `viewers/`: native desktop/AMT browser components, retained with Apache-2.0
  attribution. EndpointControl adapters connect them to the C# relay endpoints.
- `agent-binaries/`: verified development baseline binaries. `provenance.json`
  records filenames, upstream release, sizes and SHA384 hashes. Rebuilt EndpointControl
  native releases can replace them through the same reviewed update process.

EndpointControl's MIT license applies to its original orchestration/UI/adapters, not as
a replacement for the licenses of these retained components or dependencies.
