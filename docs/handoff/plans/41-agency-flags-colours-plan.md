# Plan 41 - Agency flags and colours everywhere

Base: `origin/master` = v0.30.0-agencies.9 (`c97324fd`). All file:line references are at that commit.
Assumes `fix/agency-flag-list` (`acca143b`, edits `AgencyIdentityDrawer.cs` flag picker + `AgencyIdentityWireTest.cs`) has landed first.
Requested by the server owner. Client-only work: **no server or wire-protocol changes**.

## 0. What already exists (verified)

| Piece | Where | Notes |
|---|---|---|
| Identity DTO | `LmpCommon/Agency/AgencyIdentityInfo.cs:10-17` | `AgencyId, Revision, HasColour, Red/Green/Blue, FlagUrl` (default `Squad/Flags/default`) |
| Client identity cache | `LmpClient/Systems/Agency/AgencyIdentityClient.cs` | `Get` (:31) **allocates a copy under a lock every call**; `TryColour` (:56); `RequestRefresh` (:62) sets a flag; `Update` (:64) re-tints all orbits via `PlayerColorSystem` (250 ms routine, `AgencySystem.cs:89`) |
| Broadcast to every client | `Server/Agency/AgencyIdentitySystem.cs:22-33` (snapshot on sync-all, `AgencyNetwork.cs:146`), `:95-100` (upsert to all `Supports` clients on change) | Every client already has every agency's identity |
| Vessel -> agency | `AgencySystem.GetVesselAgency` (`AgencySystem.cs:63`), map filled at `AgencyMessageHandler.cs:90-108` (calls `AgencyIdentityClient.RequestRefresh`) | Broadcast to all clients |
| Agency names + member display names | `AgencyInfo.MemberDisplayNames` (`LmpCommon/Agency/AgencyInfo.cs:21`), sent to everyone by `AgencyNetwork.SendSyncAllTo` (`Server/Agency/AgencyNetwork.cs:133-146`) | Player -> agency is derivable client-side |
| Launch-site assignments | `AgencyNetwork.cs:141-144` sends the **full** site->agency map to every client; client copy `AgencySystem.LaunchSitesSnapshot` (`AgencyLaunchSiteState.cs:12`) | No secret-site concept exists; assignments are already public |
| Orbit + **map icon** tint | `PlayerColorSystem.SetVesselOrbitColor` (`PlayerColorSystem.cs:57-66`) -> `OrbitRendererBase.SetColor` sets `nodeColor`; stock `OrbitRendererBase.objectNode_OnUpdateIcon` does `data.color = nodeColor.A(lineOpacity)` (not overridden by `OrbitRenderer`) | **Map-view / tracking-station planetarium vessel icons are already agency-tinted.** Gaps are captions, the TS list, flight labels |
| CommNet link colours | `LmpClient/Harmony/AgencyCommNetVisibility.cs` | Reference only |
| Flag + colour accent label | `AgencyWindow.DrawIdentityLabel` (`Windows/Agency/AgencyIdentityDrawer.cs:29-48`) | **Leaderboard rows and firsts already use it** (`LeaderboardDrawer.cs:44`, `:54`) and the agency tab (`AgencyDrawer.cs:82`, `:236`). Calls `GameDatabase.GetTexture` twice per row per OnGUI event |
| Visibility gate | `VisibilityClient.CanSee(Vessel)` (`Systems/Agency/VisibilityClient.cs:85`), `CanSee(Guid)` (:75), `Enabled` (:29) | `true` = fully identified (own, shared, override Allow, physics range, or sensor + identified contact). When `AgencyHideCraft` is off everything is public |
| Hidden-craft presentation | `Harmony/AgencyVisibility.cs:39-60` | Icons (`CanDrawAnyIcons`), list filter (`MapViewFiltering.CheckAgainstFilter`), flight labels (`VesselLabels.ProcessLabel` prefix :48/:73), KSC markers, orbit lines are already masked |
| Existing label hooks | `LabelEvents.cs:10-42` via `OrbitRendererBase_OnUpdateCaption.cs`, `VesselLabels_ProcessLabel.cs`, `TrackingStationWidget_Update.cs` | Prepend control-lock owner name |
| Flag textures from server | `FlagSystem.HandleFlags` (`Systems/Flag/FlagSystem.cs:50-60`, every 5 s) appends to `GameDatabase.databaseTexture` | Custom agency flags appear late; caches must retry misses |
| Missing-flag fallback on receive | `Extensions/ProtoVesselExtension.cs:149-156` | Other clients swap unknown part flags to default |

KSP facts verified by decompiling `External/KSPLibraries/Assembly-CSharp.dll` (ilspycmd) and `GameData/KerbalKonstructs/KerbalKonstructs.dll`:

- `GameDatabase.GetTexture(url, false)` is a **linear, case-insensitive scan** of `databaseTexture` -> never call it per frame.
- `KSP.UI.Screens.TrackingStationWidget`: public `TextMeshProUGUI textName/textStatus/textInfo`, `VesselIconSprite iconSprite`, `Vessel vessel`; private `Update()` resets `textName.text` only when it differs from `vessel.DiscoveryInfo.displayName.Value`. `VesselIconSprite` holds a private serialized `Image image`.
- `MapNode.CaptionData { Header, captionLine1..3 }`, `FormatCaption` wraps Header in `<b>` (TMP rich text). `MapNode.IconData.color`.
- `ShipConstruction.AssembleForLaunch(..., string flagURL, ...)` (internal 13-arg overload, already patched) runs `part.flagURL = flagURL` for every part; `FlagDecal.UpdateFlagTexture` reads `part.flagURL` on start. `ShipConstruct.missionFlag`, `EditorLogic.FlagURL`.
- `LaunchSite { Body, spawnPoints[] (latitude, longitude, altitude), GetWorldPos(), UpdateNodeCaption(MapNode, CaptionData) }`; `PSystemSetup.SpaceCenterFacility { hostBody, spawnPoints[] }`; stock map site markers are `SiteNode` (only stock/MH sites + KSC).
- KK `KerbalKonstructs.Core.KKLaunchSite`: public `float refLat, refLon, refAlt`, `CelestialBody body`, `string LaunchSiteName`, property `bool LaunchSiteIsHidden`.
- Cameras: flight `FlightCamera.fetch.mainCamera`; KSC scene `Camera.main` (what `AnchoredDialog`/`KSCVesselMarker` uses); map/TS `PlanetariumCamera.Camera` + `ScaledSpace.LocalToScaledSpace` (pattern in `VisibilityContactOverlay.Project`, `:55`).
- `CameraManager.Instance.currentCameraMode` enum `Flight, Map, External, IVA, Internal`; `FlightGlobals.VesselsLoaded`.

Build trap: `LmpClient/LmpClient.csproj` is an old-style project with explicit `<Compile Include>` items. Every new client file must be listed there. `LmpCommon` and `LmpCommonTest` are SDK-style (no csproj edits).

## 1. Cross-cutting rules

**Privacy.** A vessel gets agency styling (colour, flag, agency name) only when `owner != Guid.Empty && VisibilityClient.CanSee(vessel)`. Hidden vessels already lose icons, labels and list rows; styling code must still check, because visibility changes between the 250 ms presentation refreshes. Player->agency and site->agency data is already broadcast to everyone, so chat, player list, leaderboards and site flags reveal nothing new. Respect KK `LaunchSiteIsHidden`.

**Default-identity noise.** Draw a flag only when the identity's `FlagUrl` is not the stock default; apply colour only when `HasColour`. On servers without the identity protocol (`AgencyIdentityClient.Supported == false`), everything degrades to "no flag, no colour" with no errors.

**Readability.** User colours can be near-black. Text tints use `ReadableTextColour` (lift luminance to a floor, keeping the hue). Icon tints use the raw colour.

**Performance.**
- One shared, main-thread-only cache (`AgencyPresentation`, S0) keyed by agency id, rebuilt only when `AgencyIdentityClient.Version` changes. It holds the `Color`, the readable text colour, the resolved `Texture2D`, the escaped TMP prefix string and a `GUIContent`. No `Get()` copies on hot paths.
- Flag texture cache: `Dictionary<string, Texture2D>` (OrdinalIgnoreCase). On a miss, store null and retry only when `GameDatabase.Instance.databaseTexture.Count` changes or 5 s pass. Fall back to null, never to a per-frame lookup.
- No LINQ, no string concatenation and no `new` in per-frame code. Candidate lists refresh at 4 Hz into reusable arrays. Overlays draw only on `EventType.Repaint`.

**Toggles.** These are client settings in `SettingStructure`, all default **on**. The XML serializer tolerates new properties.
`AgencyTintVessels`, `AgencyTrackingListFlags`, `AgencyAutoCraftFlag`, `AgencyNameplates`, `AgencyNameplateRangeKm` (float, default 2.5, clamp 0.2-25), `AgencySiteFlags`, `AgencyChatPlayerList`, `AgencyWindowFlags`.

## 2. Slices and file ownership

S0 lands first and is small. S1-S6 then run in parallel. No two slices edit the same file.

### S0 - Foundation (Sonnet)

New files:
- `LmpCommon/Agency/AgencyPresentationPolicy.cs` (pure):
  - `bool ShouldStyle(bool featureOn, Guid owner, bool canSee)`
  - `bool ShowFlag(AgencyIdentityInfo)` (safe URL and not the default)
  - `string ColourHex(byte r, byte g, byte b)` -> `#RRGGBB`
  - `(byte,byte,byte) ReadableTextColour(byte r, byte g, byte b, double minLuminance = 0.45)`
  - `string EscapeTmp(string)` wraps in `<noparse>...</noparse>` after removing any `</noparse>`; also caps length at 64
  - `IReadOnlyDictionary<string, Guid> ResolvePlayerAgencies(IEnumerable<(Guid Id, string[] Names)>)`: display name -> agency, with names claimed by two agencies dropped, ordinal and case-sensitive like LMP player names
  - `bool ShouldRetryTexture(long lastMissTicks, long nowTicks, int lastDbCount, int dbCount)`
- `LmpCommonTest/AgencyPresentationPolicyTest.cs` covers each function: default-flag suppression; escape of `</noparse>` injection; black/white/saturated readability; ambiguous member names; retry timing and count change.
- `LmpClient/Systems/Agency/AgencyPresentation.cs` (main thread only):
  - `bool TryGetAgencyStyle(Guid agency, out AgencyStyle style)`. `AgencyStyle` is a class instance reused per agency and holds `HasColour`, `Colour`, `TextColour`, `Flag` (Texture2D or null), `Name`, `NameGuiContent`, `TmpPrefix`.
  - `bool TryGetVesselStyle(Vessel v, out AgencyStyle style)`, which applies `ShouldStyle` with `VisibilityClient.CanSee(v)`.
  - `Guid GetPlayerAgency(string playerName)`. The map is rebuilt at most once per second from `AgencySystem.Singleton.KnownAgencies`, and only when a cheap fingerprint (count plus summed member counts plus the agency ids' hash) changes.
  - `Texture2D GetFlagTexture(string url)` (the cache above). `Clear()` is called from `AgencySystem.OnDisabled` through `AgencyIdentityClient.Clear`.
- `LmpClient/Windows/Agency/AgencyBadge.cs`: IMGUI helpers `DrawFlag(Guid agency, float w, float h)` (GUILayout) and `DrawFlag(Rect, Guid)`. Each is a no-op when there is no flag. All reads come from the cache.
- Stub files, with empty bodies that the later slices own: `Harmony/AgencyPresentationPatches.cs` (`Install(harmony)` calling the per-feature installers below, plus a `MainSystem.OnGUI` postfix that calls `AgencyNameplateOverlay.Draw()` and `LaunchSiteFlagOverlay.Draw()`, each wrapped in try/catch with `PlaytestDiagnostics.Write`), `Harmony/AgencyMapPresentation.cs`, `Windows/Agency/TrackingWidgetDecoration.cs`, `Windows/Agency/AgencyNameplateOverlay.cs`, `Windows/Agency/LaunchSiteFlagOverlay.cs`, `Systems/Agency/LaunchSiteLocator.cs`, `Harmony/AgencySiteCaption.cs`.

Edits:
- `AgencyIdentityClient.cs`: add `private static int version; internal static int Version => Volatile.Read(ref version);`, increment it with `Interlocked.Increment` inside `RequestRefresh` (:62), and call `AgencyPresentation.Clear()` from `Clear()` on the main thread (it is called from `AgencySystem.OnDisabled`, which is main thread).
- `SettingsStructures.cs`: add the eight toggles after `VesselSyncDiagnosticsEnabled` (:53).
- `Windows/Options/OptionsDrawer.cs`: add a `DrawAgencyDisplaySettings()` call after `DrawNetworkSettings();` (:85). It is a collapsible section with one toggle per feature plus the range slider. Each change saves settings. Changing `AgencyTintVessels` calls `AgencyIdentityClient.RequestRefresh()`.
- `Base/HarmonyPatcher.cs`: add `AgencyPresentationPatches.Install(HarmonyInstance);` in `PatchOptionalMods` after `AgencyVisibility.Install` (:65).
- `LmpClient.csproj`: add `<Compile Include>` for **every** new file of S0-S6 (listed above), so later slices never touch it.

### S1 - Map view and tracking station tint, TS list flag (Feature 1) (Sonnet)

Owns: `Systems/PlayerColorSys/PlayerColorSystem.cs`, `Systems/PlayerColorSys/PlayerColorEvents.cs`, `Systems/Label/LabelEvents.cs`, `Harmony/AgencyMapPresentation.cs`, `Windows/Agency/TrackingWidgetDecoration.cs`.

1. **Icons (map + TS planetarium).** These already follow `nodeColor`. Gate the agency branch of `SetVesselOrbitColor` (`PlayerColorSystem.cs:61-63`) on `SettingsSystem.CurrentSettings.AgencyTintVessels`. Re-tint on scene ready: in `OnEnabled` add `GameEvents.onLevelWasLoadedGUIReady` -> `AgencyIdentityClient.RequestRefresh()`, plus a second request about 1 s later through a one-shot routine. The reason is the race below: stock `OrbitRenderer.Start` calls `SetColor(grey)` and may run after `onVesselCreate`.
2. **Map caption** (`LabelEvents.OnMapLabelProcessed`, :22-31). When `AgencyPresentation.TryGetVesselStyle` succeeds, set `Header = style.TmpPrefix + Header`. `TmpPrefix` is cached as `<color=#hex><noparse>Agency</noparse></color>\n`. This keeps the existing owner prefix. Header strings already allocate today only while a caption is visible; the prefix itself is cached.
3. **Flight labels** (`LabelEvents.OnLabelProcessed`, :10-20). Set `label.text.color = style.TextColour` for styled vessels and restore the white default otherwise. This runs after the visibility prefix has already disabled hidden labels.
4. **TS list** (`LabelEvents.OnMapWidgetTextProcessed`, :33-42, fired every `TrackingStationWidget.Update`). Get or add a `TrackingWidgetDecoration` component (cache it on first sight with `GetComponent`). It stores the original `textName.color`, the icon `Image` (`iconSprite.GetComponentInChildren<Image>(true)`) and its colour, the last vessel id, the last `AgencyIdentityClient.Version` and a `RawImage` flag child. It only re-evaluates when one of those changes or every 0.5 s (visibility), so a frame costs one compare.
   - Tint `textName.color` with the readable colour and the icon `Image.color` with the raw colour.
   - Flag: a `GameObject("LmpAgencyFlag")` with a `RawImage` (texture from the cache, 24x15), parented to `textName.rectTransform.parent` and anchored at `textName`'s left-middle. Push the name right with `textName.margin = new Vector4(origLeft + 28, ...)`. Hide it (`SetActive(false)`) and restore the margin when not styled or when `AgencyTrackingListFlags` is off.
   - Also fix the existing per-frame allocation: cache the composed `"(owner) name"` string in the decoration and assign it only when the display name or the owner changed. Today it is rebuilt every frame, and KSP resets it every frame.
5. `AgencyMapPresentation.Install`: the existing attribute patches cover every hook above, so this stays empty unless the in-game check shows that the scene-load re-tint needs a `OrbitRenderer.Start` postfix (the fallback for the grey-reset race).

### S2 - Auto agency flag on launch (Feature 2) (Sonnet)

Owns: `Harmony/ShipConstruction_AssembleForLaunch.cs`, `LmpCommon/Agency/AgencyCraftFlagPolicy.cs`, `LmpCommonTest/AgencyCraftFlagPolicyTest.cs`.

- Policy, pure: `bool ShouldApply(bool featureOn, Guid myAgency, string agencyFlag, bool agencyFlagInstalled, string incomingFlag, string playerDefaultFlag, string gameFlag)`. It returns true only when all of these hold: the feature is on; `myAgency != Empty`; the agency flag is safe, not the stock default, and installed; and `incomingFlag` is null or empty, or equals (OrdinalIgnoreCase) `playerDefaultFlag` (`SettingsSystem.CurrentSettings.SelectedFlag`), `gameFlag` (`HighLogic.CurrentGame.flagURL`) or `Squad/Flags/default`.
  - Decision: a craft whose flag is the player's default counts as "not explicitly set". A per-craft flag chosen in the editor that differs from the default is kept. Picking your own default explicitly is indistinguishable from not picking, which is accepted and documented; the toggle is the opt-out.
- Hook: change the prefix signature (`:23`) to `ref string flagURL`. When `fromShipAssembly && ship != null` and the policy passes, set `flagURL = agencyFlag` before stock assigns `part.flagURL` (stock `ShipConstruction` loop, `part.flagURL = flagURL`). `agencyFlag` comes from `AgencyIdentityClient.Get(MyAgencyId).FlagUrl`; installed means `GameDatabase.Instance.ExistsTexture`.
  - Do **not** touch `ship.missionFlag`, so tooling and trade design fingerprints computed in the postfix (`:36-37`) stay unchanged.
  - The vessel is always launched into the launcher's own agency, so "own agency only" holds by construction.
  - If the flag is not installed yet (the custom flag syncs every 5 s), keep the player flag and log once per session.
- Other clients receive the parts' `flagURL` through the normal proto sync; `ProtoVesselExtension.cs:149-156` already falls back if they lack the texture. This covers KSC launch-dialog launches and the KspControl MCP too, since both go through `FlightDriver.StartWithNewLaunch` -> `AssembleForLaunch`.
- Tests cover the matrix of the inputs above, including case-insensitive matches, a default agency flag, a not-installed flag and no agency.

### S3 - Flight nameplates over rival craft (Feature 3) (Opus to design the draw path, Sonnet to implement)

Owns: `Windows/Agency/AgencyNameplateOverlay.cs`, `LmpCommon/Agency/NameplatePolicy.cs`, `LmpCommonTest/NameplatePolicyTest.cs`.

- Policy, pure:
  - `bool ShouldShow(bool featureOn, Guid owner, Guid mine, bool isActiveVessel, bool canSee, VesselTypeCode type, double distSq, double maxKm)`. It requires a rival owner (`owner != Empty && owner != mine`), `canSee`, a type other than Debris, Flag, SpaceObject or Unknown (EVA is allowed), and `distSq <= (maxKm*1000)^2`.
  - `float Alpha(double dist, double max)`: full up to 70% of range, then a linear fade to 0.25.
  - `int ClampCount = 16`.
- Refresh (4 Hz, driven from `Draw` by checking `Time.unscaledTime`): only in `HighLogic.LoadedSceneIsFlight`, when `FlightGlobals.ready` and `FlightGlobals.ActiveVessel` are set and the map is not open. Iterate `FlightGlobals.VesselsLoaded` with a for-loop and fill a fixed `Plate[16]` array (vessel ref, agency id, distance) by insertion, keeping the nearest. `canSee = VisibilityClient.CanSee(v)` is evaluated here (it locks, so 4 Hz only).
- Draw (`EventType.Repaint` only): skip it when the map is enabled, `CameraManager.Instance.currentCameraMode` is IVA or Internal, the UI is hidden (track `GameEvents.onHideUI/onShowUI` in a static subscribed at Install), or the setting is off.
  - For each plate: `cam = FlightCamera.fetch.mainCamera`, `p = cam.WorldToScreenPoint(v.CoMD)` (skip when `z <= 0` or off screen), screen y = `Screen.height - p.y - 34`.
  - `GUI.DrawTexture` the flag (24x15) when present, then `GUI.Label` with a cached `GUIStyle` (`richText = false`, bold, shadowed) whose `normal.textColor` is set from `style.TextColour` with alpha, and `style.NameGuiContent` (no strings built per frame).
- Re-check the vessel on draw: if it was destroyed (`!v`) or lost visibility since the last refresh, drop it. The visibility part is handled by the next refresh; 250 ms latency is acceptable because the stock label for that vessel is masked independently by `AgencyVisibility.Label`.
- Privacy: plates are only for loaded vessels (within physics range), and `CanSee` true is the same bar stock labels use, so nothing beyond the visible model is revealed. Plates never show for `owner == Empty`.

### S4 - Agency flag at launch-site markers (Feature 4) (Sonnet)

Owns: `Systems/Agency/LaunchSiteLocator.cs`, `Windows/Agency/LaunchSiteFlagOverlay.cs`, `Harmony/AgencySiteCaption.cs`, `LmpCommon/Agency/LaunchSiteFlagPolicy.cs`, `LmpCommonTest/LaunchSiteFlagPolicyTest.cs`.

- Secrecy check result: none on master. The whole site->agency map is sent to every client (`AgencyNetwork.cs:141-144`). Gate on `SettingsSystem.ServerSettings.AgencyLaunchSitesPerAgency && snapshot.Ready`, and skip KK sites with `LaunchSiteIsHidden == true`. If secret sites are added later, this policy is where to filter (an `isSecret` input, defaulting to false).
- Policy: `bool ShouldShow(bool featureOn, bool perAgencySites, bool ready, Guid assigned, bool kkHidden)`, `bool AboveHorizon(double[3] site, double[3] bodyCentre, double bodyRadius, double[3] camera)` (plain doubles so it is testable without Unity; ray-sphere test), and `bool WithinKscRange(distSq, 50 km)`.
- `LaunchSiteLocator` rebuilds a `List<SiteMark>` (id, body, lat, lon, alt, agency) when `LaunchSitesSnapshot.Revision` or `LaunchSiteCatalog.GetSnapshot()` reference changes:
  - Stock pad/runway: `PSystemSetup.Instance.SpaceCenterFacilities[i]` -> `hostBody`, `spawnPoints[0].GetSpawnPointLatLonAlt(...)`.
  - Stock or MH: `PSystemSetup.Instance.LaunchSites` -> `Body`, `spawnPoints[0]`.
  - KK: `KkLaunchSiteIntegration.Sites()` / `SiteId` (`KkLaunchSiteIntegration.cs:97-103`, internal), with field access through cached `AccessTools.Field` for `refLat/refLon/refAlt/body` and `AccessTools.PropertyGetter` for `LaunchSiteIsHidden`. These are resolved once; if any is missing, KK flags are disabled with one log line.
- Overlay draw (Repaint):
  - SPACECENTER: `Camera.main`, world = `body.GetWorldSurfacePosition(lat, lon, alt + 30)`, range and horizon filtered.
  - TRACKSTATION or flight map: `PlanetariumCamera.Camera`, `ScaledSpace.LocalToScaledSpace(world)`, then horizon-tested against the body sphere.
  - Draw the flag at 32x20, with a 2 px colour bar under it when `HasColour` and the agency name on hover only (`Rect.Contains(Event.current.mousePosition)`).
  - Positions are recomputed each frame (bodies rotate) with no allocations; there are typically fewer than 40 assigned sites.
- `AgencySiteCaption`: a manual postfix on `LaunchSite.UpdateNodeCaption(MapNode, MapNode.CaptionData)` that sets `captionLine3 = "Assigned: <color=#hex>" + EscapeTmp(agencyName) + "</color>"` (the string is cached per site and identity version) for stock/MH site nodes in the map. KK draws its own icons, so the overlay covers those.

### S5 - Chat and player list (Feature 5) (Sonnet)

Owns: `Windows/Chat/ChatDrawer.cs`, `Windows/Chat/ChatWindow.cs`, `Windows/Status/StatusDrawer.cs`, `Windows/Status/StatusWindow.cs`.

- Player list (`StatusDrawer.DrawPlayerEntry`, :121-142): `agency = AgencyPresentation.GetPlayerAgency(playerStatus.PlayerName)`. When the toggle is on and a style exists, call `AgencyBadge.DrawFlag(agency, 20, 12)` before the name and use the agency `TextColour` for the cached name style; otherwise use the player colour.
  - Style cache: key `_playerNameStyle` by player name and also store the `AgencyIdentityClient.Version` and agency id it was built for. Rebuild the entry when they differ; this extends the `ColorEventHandled` reset in `StatusWindow.DrawGui` (:59-63).
- Chat (`ChatDrawer.DrawChatMessageBox`, :45-49): wrap each message in `BeginHorizontal`, call `AgencyBadge.DrawFlag(agency, 16, 10)` when present, and set the text colour from the agency `TextColour` (falling back to `GetPlayerColor(chatMsg.Item1)` as now).
  - The chat buffer holds at most 500 rows; the per-row work is one dictionary lookup.
  - Server/console messages (`ConsoleIdentifier`) resolve to no agency.
- No privacy impact: membership is already public via `KnownAgencies`.

### S6 - Agency window: leaderboard polish, trade offers, bought stock (Features 6 and 7) (Sonnet)

Owns: `Windows/Agency/AgencyIdentityDrawer.cs` (only `DrawIdentityLabel`, :29-48), `Windows/Agency/LeaderboardDrawer.cs`, `Windows/Agency/TradeDrawer.cs`, `Windows/Agency/AgencyDesignsDrawer.cs`. Rebase onto master after `fix/agency-flag-list` merges, because it edits other parts of `AgencyIdentityDrawer.cs`.

- Feature 6 is **already shipped**: leaderboard and firsts call `DrawIdentityLabel` (`LeaderboardDrawer.cs:44`, `:54`). The remaining work:
  - Reimplement `DrawIdentityLabel` on `AgencyPresentation` and `AgencyBadge`, so there are no per-event `GetTexture` scans or `Get()` copies.
  - Honour `AgencyWindowFlags`.
  - Keep the 8 px colour accent, and keep the flag slot width so the rows stay aligned.
- Trade offers (`TradeDrawer.DrawTradeOffers`, :74): replace the heading `Label` with `BeginHorizontal` + `AgencyBadge.DrawFlag(counterparty, 32, 20)` + heading.
  - The counterparty is the seller for incoming offers and the buyer for outgoing ones, which matches the existing name logic.
  - Do the same in `DrawReceivedTrades` (:334, `design.SellerAgencyId`) and the buyer list in `DrawNewTrade` (:159-160, flag before each toggle).
- Bought stock (`AgencyDesignsDrawer.DrawBoughtRow`, :406-410): put the flag of `row.SourceAgency` beside the heading at :408 when `row.SourceAgency != Guid.Empty`.

## 3. Tests

- LmpCommonTest (MSTest), pure logic only: `AgencyPresentationPolicyTest` (S0), `AgencyCraftFlagPolicyTest` (S2), `NameplatePolicyTest` (S3), `LaunchSiteFlagPolicyTest` (S4).
  - Run with `C:/Users/james/.dotnet/dotnet.exe test LmpCommonTest/LmpCommonTest.csproj -c Release`.
  - Also build the full `LunaMultiPlayer.sln -c Release`; LmpClient is not covered by tests.
- In-game check (informational, not a gate), with two clients in different agencies, both with colours and custom flags:
  1. TS: icons and list rows are tinted, the flag sits left of the name and text is not clipped, and a hidden rival (with `AgencyHideCraft` on) shows no row or flag.
  2. Map: the caption header carries the coloured agency name.
  3. Launch from VAB with the default flag gives the agency flag on flag decals. With an explicit per-craft flag, that flag is kept. The rival client sees the same flag.
  4. Flight within 2.5 km of a rival: a nameplate shows, then disappears when out of range, in IVA, in the map, with F2, and when visibility is denied.
  5. KSC: the flag shows over the assigned pad/runway and KK sites; TS shows flags over sites; KK hidden sites show none.
  6. Chat and player list: flag and agency colour.
  7. Agency window: leaderboard, firsts, offers, received designs and bought stock each show the flag.
  8. Each toggle off restores stock or LMP visuals without a restart.
- Profile: with 30 vessels and 10 agencies in the TS, check that the `GC.Alloc` per frame in `TrackingStationWidget.Update` does not exceed the pre-change baseline.

## 4. Risks

| Risk | Mitigation |
|---|---|
| `OrbitRenderer.Start` resets vessel colour to grey after our tint (race at scene load) | Re-tint on `onLevelWasLoadedGUIReady` and again about 1 s later (S1); verify in TS |
| TS widget layout: the RawImage overlaps or clips the name (prefab hierarchy not inspected) | Use TMP `margin` rather than reparenting; fallback is to put the flag at the right edge of the `textInfo` row; screenshot check |
| `GameDatabase.GetTexture` cost (linear) | Single cache with miss retry (S0); S6 removes the existing per-event scans |
| Custom agency flag not yet loaded on a client (5 s `FlagSystem` cadence) | Cache retries when `databaseTexture.Count` changes; S2 skips applying until installed |
| User-controlled agency names in TMP captions (rich-text injection) | `EscapeTmp` with `<noparse>` plus stripping `</noparse>`, unit-tested; IMGUI styles keep `richText=false` |
| Unreadable dark colours on dark UI | `ReadableTextColour` for text; raw colour only for icons and accents |
| KK reflection drift | Resolve fields once and disable KK site flags with a diagnostic; the stock path is unaffected |
| Two `MainSystem.OnGUI` postfixes (existing `AgencyVisibility.DrawContacts` plus the new host) | Independent try/catch; order does not matter |
| Merge conflict with `fix/agency-flag-list` in `AgencyIdentityDrawer.cs` | S6 starts after that fix merges and touches only `DrawIdentityLabel` |
| `LmpClient.csproj` contention | S0 pre-registers every new file; later slices never edit it |
| Visibility latency (250 ms) between hide and plate removal | Plates are refreshed at 4 Hz and only exist inside physics range, where the craft model is visible anyway |
| Behaviour change: `AgencyTintVessels` off now drops the agency orbit and icon tint already on master | Defaults on, so the current behaviour is preserved |

## 5. Order

1. S0, which takes about half a day; merge it.
2. S1-S6 in parallel worktrees (`feat/agency-presentation-s1..s6`). Each does one review pass, then they merge in any order. S6 waits for `fix/agency-flag-list`.
3. Run the integrated build plus the LmpCommonTest run, do the two-client in-game pass, and write release notes.

## Revision 1 (plan review; R1 wins)

Where this section disagrees with anything above, this section wins. Base is now `origin/feat/agencies-10` (agencies.9 + flag picker fix `3aea2303`).

### Medium

- **M1 - S1.4 TS list owner prefix.** Stock `TrackingStationWidget.Update` resets `textName.text` to the discovery display name every frame, so "assign only on change" would let the prefix flicker away. The decoration caches the composed `"(owner) name"` string (rebuilt only when the display name, owner or identity version changes) but **assigns it every frame**. The per-frame cost is one reference assignment, no allocation.
- **M2 - S1.3 flight label colour.** Only set `label.text.color` when the vessel is styled; **never restore** a colour (stock and other hooks own the unstyled colour). Skip the tint entirely for the current target vessel so the stock `XKCDColors.ElectricLime` target highlight is kept.
- **M3 - S1.1 orbit/icon tint.** Stock `OrbitRenderer.Start` sets the grey colour, so the **main mechanism** is an `OrbitRenderer.Start` postfix (installed by `AgencyMapPresentation.Install(Harmony)`) that calls `PlayerColorSystem.Singleton.SetVesselOrbitColor(__instance.vessel)` when `__instance.vessel` is set. The `onLevelWasLoadedGUIReady` refresh stays as a cheap backup; the 1 s one-shot re-request is dropped.
- **M4 - S6 base.** S6 waits for `feat/agencies-10`, which is already the base for this work; `3aea2303` touched `AgencyIdentityDrawer.cs` and `FlagSystem.cs`. S6 still edits only `DrawIdentityLabel` in `AgencyIdentityDrawer.cs`, and nobody edits `FlagSystem.cs`.

### Low

- **S2 rationale.** The design fingerprint and tooling charge are computed from the craft file at `BeginLaunch`, not from part `flagURL`s, so rewriting `flagURL` in `AssembleForLaunch` cannot change them (the `missionFlag` note above is belt and braces). Add an in-game check: launch the same design with the toggle on and off and confirm the same tooling charge and fingerprint.
- **S1.4 TS flag image.** The flag `RawImage` sets `raycastTarget = false` (it must not eat widget clicks), and its GameObject gets a `LayoutElement` with `ignoreLayout = true` so any layout group on the parent does not reflow the row.
- **S4 KK positions.** Use the KK site's public `StaticInstance staticInstance` field (verified in `KerbalKonstructs.dll`) and its `RadialPosition` / transform position, not `refLat/refLon` (KK rounds those to 2 decimals from `RadialPosition`). While `KkLaunchSiteIntegration.CatalogPending` is true, the locator retries its rebuild (at most once per second) instead of caching an empty KK set.
- **S4 stock site captions.** Stock pad/runway markers are `KSCSiteNode` (an **internal** class, so resolve it with `AccessTools.TypeByName("KSCSiteNode")`; it has `public void UpdateNodeCaption(MapNode, MapNode.CaptionData)`), not `LaunchSite` nodes. Either patch `KSCSiteNode.UpdateNodeCaption` the same way as `LaunchSite.UpdateNodeCaption`, or document in the slice that pad/runway captions are covered by the overlay only.
- **S3 UI hidden state.** Reuse `MainSystem.ToolbarShowGui` instead of tracking `onHideUI/onShowUI` separately, and gate `Draw()` on the network state (connected / running) before any work.
- **S0 EscapeTmp.** Strip `</noparse>` **case-insensitively** and repeat until none remain (so `</no</noparse>parse>` cannot reassemble), then cap and wrap. Unit-test the nested case.
- **S0 options slider.** The nameplate range slider saves settings on mouse release, or when the value moved more than a small threshold since the last save, never every frame while dragging.
- **S0 pinned stubs.** S0 writes `AgencyPresentationPatches.cs` in final form, calling these exact signatures, so S1/S3/S4 never edit it:
  - `internal static void AgencyMapPresentation.Install(HarmonyLib.Harmony harmony)` (S1)
  - `internal static void AgencySiteCaption.Install(HarmonyLib.Harmony harmony)` (S4)
  - `internal static void AgencyNameplateOverlay.Draw()` (S3)
  - `internal static void LaunchSiteFlagOverlay.Draw()` (S4)
- **S5 chat label.** The chat message label uses `GUILayout.ExpandWidth(true)` (or a max width) so wrapping still works once a flag sits in the same horizontal row.
