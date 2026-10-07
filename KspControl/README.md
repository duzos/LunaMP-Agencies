# KSP control foundation

This is an unreleased, read-only MCP/bridge foundation. It does not build, save,
launch or fly craft, invoke part controls, run science, spend funds, or grant
itself authority. Live KSP and native Claude-to-game acceptance remain separate
checks; passing protocol tests is not proof of game operation.

## Projects

- `Contracts`: bounded version-1 length-prefixed JSON DTOs for .NET and Unity Mono.
- `Host`: .NET 10 official MCP SDK stdio server; logs use stderr.
- `Bridge`: net472 KSP addon. An authenticated loopback listener queues observations
  for Unity's main thread. No token configuration means no listener.
- `EditorModel`: detached craft graph, tree/catalog/stage/symmetry validation and
  stack-node transform arithmetic. No native craft export/import yet. The installed
  stock `.craft` format was inspected, but valid export requires configured native
  part/module snapshots, ID allocation and game roundtrip proof; it is not safe to
  invent those defaults from a graph alone. Graph validation does not establish
  aerodynamic stability, collision clearance, fuel compatibility or launchability.
  Stack node alignment and opposing normals use the separate StackGeometryValidator gate;
  graph validation alone is not permission to import.
- `Host/ControlJournal`: unexposed authority/job primitive. Reservations and request
  identities persist; in-flight work becomes indeterminate after restart. Grants
  and leases must be re-established through a future trusted integration. Revoked
  grant IDs cannot be reused. No mutation dispatch is wired to MCP.

## Build and test

From the repository root with the installed SDK:

```powershell
& C:/Users/james/.dotnet/dotnet.exe build KspControl/Host/KspControl.Host.csproj -c Release
& C:/Users/james/.dotnet/dotnet.exe build KspControl/Bridge/KspControl.Bridge.csproj -c Release
& C:/Users/james/.dotnet/dotnet.exe test KspControl/HostTests/KspControl.HostTests.csproj -c Release
& C:/Users/james/.dotnet/dotnet.exe test KspControl/Bridge.Tests/KspControl.Bridge.Tests.csproj -c Release
& C:/Users/james/.dotnet/dotnet.exe test KspControl/EditorModel.Tests/KspControl.EditorModel.Tests.csproj -c Release
```

## Connection contract

Run the built `KspControl.Host.dll` through .NET with stdin/stdout attached to the
MCP client. The host does not launch KSP. Both processes require
`KSP_CONTROL_TOKEN_FILE` pointing at the same locally protected credential file
(32-256 characters, at most 512 bytes); never include its contents in prompts or
logs. `KSP_CONTROL_PORT` optionally overrides loopback TCP port 43819. Provisioning,
installation, game lifecycle and client configuration are orchestrator tasks, not
MCP tools. An absent credential produces `credential_not_configured`.

Current tools are `capabilities`, `context`, `parts`, `editor`, `vessel`,
`part_controls`, `science`, `part_definition` and `editor_snapshot`. The latter two provide bounded configured-part and native editor snapshots; they do not import or create craft. Part and vessel pages are bounded. Part controls
and science require a part ID from an accessible craft observation. Controls are
descriptors only; field values/invocation are not available. Flight inspection
requires a ready LunaMP ownership record belonging to the current agency. No
foreign-vessel enumeration is exposed. Revision values are observation sequences,
not mutation preconditions. Screenshots, MechJeb, contacts and write operations are
explicitly unavailable pending their implementations and acceptance gates.




## Offline preview packaging

`./KspControl/Build-Package.ps1` builds the bridge and publishes a self-contained
Windows x64 host, then verifies a ZIP allowlist and SHA-256 manifest under a new
`Artifacts/KspControl-preview-*` directory. It refuses tracked source changes or
a source commit change during the build. It does not install, update, launch,
publish or release anything. The GameData payload contains only bridge/contracts
DLLs; it requires the matching LunaMP client facade and existing Newtonsoft.Json
13.0.0.0 assembly. This preview is explicitly unvalidated in live KSP.
