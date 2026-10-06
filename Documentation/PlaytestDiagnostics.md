# Playtest diagnostics

Enable these independently on the client and server before reproducing a problem. Both default to off. No protocol change or matching flag setting on the other side is required.

## Client

In the LMP Options window, enable **Verbose diagnostics**. This saves `VerboseDiagnostics` in `GameData/LunaMultiplayer/Data/settings.xml`. To capture startup and Harmony attachment paths, enable it and restart KSP, or set `<VerboseDiagnostics>true</VerboseDiagnostics>` in that XML while KSP is closed.

The additional `[LMP-DIAG]` lines go to the normal `KSP.log` in the KSP installation directory. Copy the log after reproducing the problem, before starting another KSP session. The existing vessel-sync diagnostics setting remains independent.

## Server

For one run, add `--verbose-diagnostics` to the server command line. For example:

```powershell
dotnet Server.dll --data-directory "D:/LMP-test-data" --verbose-diagnostics
```

Use a .NET 10 host. Alternatively, set `<VerboseDiagnostics>true</VerboseDiagnostics>` inside the root element of `Config/DebugSettings.xml` under the selected data directory and restart the server. The settings loader writes missing settings with defaults on startup; you can also add the element manually while the server is stopped. Remove the command-line flag and set the XML value to false to disable it; either source can enable diagnostics.

Server output is written to its normal console and `logs/lmpserver_*.log` under the selected data directory. Enabling this flag does not require increasing the separate log-level setting.

## What to send

Send the client `KSP.log` and the matching server log, along with approximately when the problem happened, the action you took and what you expected. Send both client logs if more than one player participated. A single-player reproduction can still provide useful evidence.

Diagnostic lines carry a local session ID, UTC time, sequence, event/path and bounded state summaries. Build IDs identify the exact assemblies. Match client/server events using time, message type, agency and vessel IDs; session IDs belong to individual processes and are not cross-network request IDs.

Coverage focuses on startup/patches, network transitions, agency actions and replies, routing, funds, scenarios, kerbals and vessel/control decisions. High-frequency traffic has a separate limit so it cannot consume the decision-event allowance. Suppression is reported when the next logging window receives an event; missing lines in a suppressed interval do not prove that a code path did not run.

New diagnostics avoid raw payloads, chat text, passwords and device identifiers. They intentionally include filesystem paths and gameplay state. The full existing application logs can also contain output from KSP, mods and older logging code; inspect them before sharing publicly.

Automated tests and server startup checks do not establish client UI or live gameplay correctness. Those remain unverified until a playtest is performed.

## Client package prerequisites

Before building the client, run `Scripts/Prepare-KspRuntime.ps1 -KspDirectory "<KSP installation>"`.
This imports the compatible Mono runtime dependencies from that installation's launcher into ignored
`External/KSPRuntime`. The client build requires these files and includes them in its output, including
before the Debug deployment step. Do not substitute SDK reference assemblies for runtime implementations.
Run `Scripts/Test-ClientRuntime.ps1 -KspDirectory "<KSP installation>" -ClientPlugins "<built plugin directory>"`
to check the dependency set. Any reported binding differences still require verification with KSP's Mono runtime.

When tooling or trading is enabled, `client.economy.snapshot` should identify a ready snapshot and a
nonempty session. A local funds/science change produces `client.economy.command` with its delta and
sequence, followed by the server's `economy.command` result and a newer snapshot. An
`operation=Delta reason=no-session` refusal means the change was not sent, even if the local funds display
changed. Investigate startup/deserialization errors before treating reconnect persistence as proof of
server accounting. `client.economy.hooks ready=True` confirms the required tooling hooks installed.
