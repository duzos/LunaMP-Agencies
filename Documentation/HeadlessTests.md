# Headless multiplayer tests

`HeadlessTest` exercises the real server over loopback UDP with three Lidgren clients. It requires .NET 10, but no KSP installation or Unity runtime. These tests verify server protocol, agency membership and chat isolation, not UI, physics or in-game presentation.

From the repository root:

```powershell
dotnet test HeadlessTest/HeadlessTest.csproj -c Release --logger trx
```

Use the installed .NET 10 host explicitly if your PATH selects an older SDK:

```powershell
& "$env:USERPROFILE/.dotnet/dotnet.exe" test HeadlessTest/HeadlessTest.csproj -c Release --logger trx
```

The child server normally uses the same .NET installation as the test runtime. Set `LMP_TEST_DOTNET` to an absolute `dotnet` executable path to override it. A missing or unusable host fails the test with startup evidence. `--no-build` requires a previous build of the same configuration: the build stages the complete server runtime beneath the test output's `server/` directory.

The solution and `Scripts/Run-AllTests.ps1` / `Scripts/run-all-tests.sh` include this project. Run the headless project by itself when only server/network coverage is needed; the full solution also builds the KSP client with its usual library prerequisites.

## Isolation and assertions

Each run generates fresh XML settings and an empty Universe beneath a uniquely named temporary directory. The child server listens only on loopback, with master-server registration, UPnP and its website disabled. STUN is directed to loopback. The ordinary server version check may still attempt an external lookup; assertions do not rely on it. No installed server or live data is copied or modified.

Three clients handshake into separate solo agencies, then create agencies and approve membership using production messages. Expected agency-change disconnects require a fresh handshake and authoritative membership sync. The chat test checks sender echo, same-agency delivery and cross-agency isolation. Subsequent global messages on the same reliable ordered channel are barriers and positive controls for the negative assertions, including rejection of a spoofed destination agency.

Startup, connection and assertion waits have deadlines. The fixture retries startup only when output confirms a UDP bind collision, and terminates that attempt before choosing a fresh port. Do not run multiple copies against the same test output directory simultaneously.

## Evidence and cleanup

MSTest attachments retain bounded stdout/stderr captures and bot transcripts outside the temporary server root. The test output includes process ID, temporary root, port and attachment directory; assertion failures include recent protocol/server evidence. Server playtest diagnostics are enabled for these isolated runs. Captures omit arbitrary payload dumps, but ordinary server logs can include player identifiers; these are synthetic test identities.

Teardown stops the bots and kills only the child server tree owned by the fixture, awaiting exit before deleting its temporary root. This deliberate kill is not evidence of graceful shutdown or disk persistence. Cleanup failures are reported without replacing an earlier scenario failure. If exit cannot be confirmed, the root is retained and reported for investigation.
