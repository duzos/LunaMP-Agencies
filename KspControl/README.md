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
  identities persist; in-flight work becomes indeterminate after restart. It mirrors
  the bridge's grant `(id, generation)` and lease id for audit; it never verifies a
  grant and the world epoch lives on the lease. A revoked `(id, generation)` pair
  cannot be reused. No mutation dispatch is wired to MCP.

## Build and test

From the repository root with the installed SDK:

```powershell
& C:/Users/james/.dotnet/dotnet.exe build KspControl/Host/KspControl.Host.csproj -c Release
& C:/Users/james/.dotnet/dotnet.exe build KspControl/Bridge/KspControl.Bridge.csproj -c Release
& C:/Users/james/.dotnet/dotnet.exe test KspControl/HostTests/KspControl.HostTests.csproj -c Release
& C:/Users/james/.dotnet/dotnet.exe test KspControl/Bridge.Tests/KspControl.Bridge.Tests.csproj -c Release   # runs on net10.0 and net472
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

Current tools are `editor_state`, `editor_engineering`, `craft_plan`, `editor_apply_craft`, `editor_restore_snapshot`, `job_status`, `control_status`, `control_acquire_lease`, `control_renew_lease`, `control_release_lease`, `capabilities`, `context`, `parts`, `editor`, `vessel`,
`part_controls`, `science`, `part_definition` and `editor_snapshot`. The latter two provide bounded configured-part and native editor snapshots; they do not import or create craft. Part and vessel pages are bounded. Part controls
and science require a part ID from an accessible craft observation. Controls are
descriptors only; field values/invocation are not available. Flight inspection
requires a ready LunaMP ownership record belonging to the current agency. No
foreign-vessel enumeration is exposed. Revision values are observation sequences,
not mutation preconditions. Screenshots, MechJeb, contacts and write operations are
explicitly unavailable pending their implementations and acceptance gates.

`craft_plan` (read-only, no lease) validates a part graph, fetches node data for the parts it names through the bridge's `parts.construction_catalog` operation (at most 32 names per call, folded into the tool), and runs the pure planner on the host: `issues[]`, `planHash`, `catalogHash`, `topology` T1/T2/T3, computed transforms and staging fields for every part, and symmetry groups. `constructionSupport` is `verified` only for parts in the bridge's evidence table (`ConstructionSupportPolicy.VerifiedTable`: mk1pod.v2, fuelTankSmall, liquidEngine.v2, probeCoreOcto.v2, Decoupler.1); everything else is refused by the planner until live verification extends the list. `_modVersions` is apply-time data and is reported as `not_included`. The latter two provide bounded configured-part and native editor snapshots; they do not import or create craft. Part and vessel pages are bounded. Part controls

Argument bounds (shared `ObservationLimits`, advertised in tool schemas): `offset`
0..100000, `limit` 1..50 (`editor_snapshot` 1..20), `filter` up to 128 characters,
`part_controls` pages 4 modules, `partName` up to 256 characters, numeric `partId`.
Out-of-range input returns a normal failed envelope with `reasonCode`
`invalid_argument` and `data.detail` naming the field, without contacting the game.
`part_definition` also reports `invalid_part_name` and `definition_unavailable`.
`parts` lists buildable parts only by default; each item has `category` and
`buildable`, and `includeNonBuildable=true` adds pseudo-parts (kerbalEVA, flag),
hidden and unresearchable parts. Offsets are positions in the filtered list.
Attach nodes carry `kind` (`stack`, `surface`, `dock`); `surfaceAttachNode` has
`kind: "surface"`, `rawNodeType` and `usable`, and each part has an `attachRules`
block (`canSurfaceAttach`, `acceptsSurfaceAttach`, `stack`, `allowStack`,
`allowCollision`).




## Trust plumbing (grants, leases, inline control)

Mutations remain unavailable. This layer only decides *whether* a future mutation may
run, and it is exercised by `control_*` tools that change no game state.

**Grant.** A human-issued file `{"payload":"<base64 UTF-8 JSON>","mac":"<base64 HMAC-SHA256>"}`.
The MAC covers the exact decoded payload bytes (no canonicalisation) and the payload is
parsed only after it verifies. Payload fields: `version`, `grantId`, `generation`,
`issuedUtc`, `expiresUtc`, `binding{installId, saveFolder, agency}`, `operations[]`,
`facilities[]`, `unsavedCraftPolicy`, `maxParts`, `spendLimitFunds`, `revoked`. `agency`
is the LunaMP agency GUID, or `offline:<saveFolder>` for an isolated save (never valid
while connected to an agency). `installId` is the first 16 hex characters of SHA-256 of
the upper-cased full KSP root path.

**CLI** (a human-run branch of the host executable; never an MCP tool):

```powershell
KspControl.Host.exe grant issue --ksp-root <KSP root> --save <SaveFolder> [--agency <guid>] [--ops a,b] [--facilities VAB] [--policy refuse|snapshot_then_replace] [--max-parts N] [--hours H]
KspControl.Host.exe grant revoke | rearm [--hours H] | show
```

Key and grant live under `%LOCALAPPDATA%\KspControl	rust\` (`grant.key`: 32 random bytes,
created on first `issue`, never printed; `grant.json`). Overrides: `--trust-dir`, `--grant-file`,
`--key-file`, or env `KSP_CONTROL_TRUST_DIR`. `issue`, `revoke` and `rearm` always write
`generation + 1`; `show` never prints the key, payload or MAC. Only KSP gets the key path:
`KSP_CONTROL_GRANT_FILE` and `KSP_CONTROL_GRANT_KEY_FILE` go in KSP's environment, not the
MCP host's. Without both, no grant is ever provisioned. The key file and trust directory are created user-only on Windows (inheritance off), with a warning if that fails. The host journal lives in `%LOCALAPPDATA%\KspControl\journal\<installId>` when `KSP_CONTROL_KSP_ROOT` names the KSP root, otherwise `...\journal\default`; `KSP_CONTROL_JOURNAL_DIR` overrides.

**Bridge.** `GrantWatcher` polls the file at 1 Hz on the main thread (re-verifying on a stat
change) and publishes an immutable status: `missing`, `malformed`, `invalid_mac`, `expired`,
`revoked`, `suspended`, `binding_mismatch`, `not_yet_applicable` or `valid`. A scene change
drops only the lease; a binding change drops the grant (re-provisioned when it matches again);
the Stop button or `KSP_CONTROL_STOP_KEY` hotkey burns the current generation and persists it
in `<KSP root>/KspControlData/control/suspensions.json`, so it survives a restart until
`grant rearm` writes a higher generation. The highest generation seen per grant id is stored in the same file, so an older envelope cannot be replayed after a restart; a failed write shows as `stopPersistFailed` in `control_status` and a warning on the panel. A human edit takeover (see Editor state) revokes
the lease and starts a 30 s cooldown.

**Tools.** `control_status`, `control_acquire_lease(purpose 1..128, durationSeconds 30..300)`,
`control_renew_lease(leaseId, durationSeconds)`, `control_release_lease(leaseId)`. None accepts
or returns grant content or key material. The host keeps the lease alive with an
inline `control.heartbeat` every 1000 ms on its own connection (800 ms timeout, in-flight
beats skipped, `lease_unhealthy` after 3 failures); the bridge watchdog (2 s) is authoritative.

**Transport.** One worker thread per connection (32-socket cap). `control.*` operations are
answered inline from authority-held state and never queue; any other operation needs one of 3
queue-wait slots, otherwise `bridge_busy`. `BridgeClient` keeps disjoint read, control and
(empty) mutation allowlists. Bridge version is `0.2.0`.

Not live-verified: the scene-ready definition, the OnGUI Stop button, the hotkey and the
watchdog behaviour during a real long frame need a KSP run.

## Editor state (revision, takeover, idle)

`editor_state` and `editor_engineering` are read-only: no lease, no grant, no change to the game. Both are queued
observations served on the main thread.

**`editor_state`** returns `editorRevision` (an opaque token, `v1|epoch|generation|editRevision|fingerprint[0..12]`, base64url; null when the
craft fingerprint cannot be computed), `editRevision`, `generation`, `partCount`, `facility`, `shipName` (the editor name field), `unsaved`
(`true`, `false` or `"unknown"`), `idle`, `fsmState` (or `"unavailable"`), `busy[]` (`part_held`, `fsm_not_idle`, `modal_lock:<id>`,
`operation_running`, `launch_pending`), `capabilities` (`fsm`, `unsavedMarker`, `saveOverwriteGuard`: `available` or `unavailable`, with
the individual guard members), `lastSavedName`, `fingerprintMode` (`full` or `dirty_tracked`) and `lastPollCostMs`.

**`editor_engineering`** (`offset` 0..100000, `limit` 1..50, `includeDeltaV` default true) returns stock data only: dry/fuel mass and cost,
`allPartsConnected`, `shipPartsUnlocked`, stageable parts per stage, per-stage delta-V (`ready=false` until the stock calculation finishes), a
paged part table (craft id, parent, stack node pair or surface, symmetry group, stage, mass), the largest linked stack-node gap,
`partsStockAllowed` (`PartTechAvailable && PartModelPurchased` for every part; null in a save without research), `craftIdentifiersValid`
(the `ToolingClient.CraftIndices` rule over a native save) and provenance labels. `toolingQuote` and `launchResearchAllowance` are
`deferred_to_P3`. Invalid arguments return `invalid_argument` without contacting the game.

**Revision model.** `generation` increments when the editor's `ShipConstruct` object changes (load, new craft, undo restore).
`editRevision` is a never-decreasing counter bumped by human-input events, generation changes and fingerprint changes; it is published as
`LeaseContext.Revision`. The fingerprint is the `CraftFingerprint` projection of `ShipConstruct.SaveShip()` with the volatile-key registry
applied, plus the editor name, description and flag. A bare `onEditorShipModified`, `onEditorPartEvent` or `onEditorVariantApplied` only
marks the editor dirty: the fingerprint is rechecked (throttled to 250 ms) and nothing happens if it is unchanged, which absorbs animation
and KSPCF re-fires. Events raised while the tracker's own native save runs are ignored.

**Takeover.** Outside an operation, with a lease held, any fingerprint change, generation change, name/description/flag change, selected part
or human-input event (part picked, placed or deleted, pod picked or deleted, undo, redo, load) calls `HumanTakeover` (lease revoked, 30 s
cooldown). With no lease the same signals only bump the revision and nothing polls: the fingerprint is recaptured at each observation and at admission. Acquiring a lease re-baselines silently, so earlier edits are never a takeover. While a lease is held the fingerprint is rechecked every second
(`full` mode), or a cheap structural hash is checked every second with a full recheck on any event or every 10 s (`dirty_tracked` mode,
chosen when the measured fingerprint cost exceeds about 20 ms). The operation windows (lock, dispatch, `post_unlock_grace`) that attribute
the operation's own events are implemented and unit-tested in `EditorRevisionTracker`; no operation runner drives them yet.

**Busy.** Idle needs an FSM state in `st_idle`, `st_podSelect`, `st_offset_select`, `st_rotate_select` or `st_root_unselected`, no selected part,
and none of the known modal locks (`EditorLogic_loadDialog`, `LoadConfirmationDialog`, `SaveConfirmationDialog`, `Saving`,
`EditorLogic_dialog_softLock`, `CreatorCraftName`, `SaveCraftOverwrite`, `NewCraft`, `LoadCraft`, `SaveUpgradeFailDialog`). `LMP_ToolingLaunch`
reports `launch_pending` and `KspControl.Op` reports `operation_running`. Other input locks (for example hover locks) are ignored. The FSM,
the unsaved marker and the overwrite-guard fields are private editor members read by guarded reflection and resolved once at startup; if a
member is missing, its capability reads `unavailable` and the check falls back to the selected part and the lock list.

The `EditorModel` sources the tracker needs are linked into the bridge as source files (no new DLL, package allowlist unchanged).

Not live-verified: the FSM state names, the reflection targets (`fsm`, `undoLevel`, `undoIndexAtLastSave`, `vesselNameAtLastSave`,
`vesselNameAtLastSave_Sanitized`, `SetLastSanitizedSaveName`), the `SaveShip` cost per poll and the resulting polling mode, that an
idle editor (including deployable-panel animation and a KSPCF re-fire) keeps a stable fingerprint, and the stock part-research,
delta-V, mass and node-gap readings.

## Apply (editor_apply_craft, editor_restore_snapshot, job_status)

These change the editor, so they need a held lease (`control_acquire_lease`), a grant that lists the operation family
(`editor.replace_craft`, `editor.restore_snapshot`) and a fresh `editorRevision` token. They never take grant content or keys.

Host path (`MutationService`): argument checks with no socket (`invalid_argument`), then `lease_required` / `lease_invalid` /
`grant_operation_denied`, then the journal admits the request (same `requestId` and arguments returns the same job, a different request
under the same id is `request_id_conflict`, a host restart turns in-flight jobs `indeterminate`), then the bridge is asked and the job is
polled for up to 20 s; after that the call returns `running` and `job_status(requestId, waitSeconds 0..20)` continues it. When the host
cannot tell whether the game acted (connection lost, bridge restarted) the job is `indeterminate` and the id stays reserved.

Bridge path: admission runs in one frame on the queued observation path (`editor.apply_craft`, `editor.restore_snapshot`,
`editor.operation_status`): authority admit, token and fresh fingerprint, idle check, unsaved-craft policy, overwrite-guard refusals
(`save_overwrite_guard_unavailable`, `name_collision_unguarded`), then the bridge **re-plans** the graph against the live catalog and
refuses with `plan_changed` unless `planHash` matches, renders the structural craft with the live `_modVersions` and persistent ids, and
hands the job to `EditorOperationRunner`, a pure state machine behind `IEditorPort` that advances one step per frame:
lock, verified snapshot (`KspControlData/<save>/recovery/kc-snap-<id>.craft` plus metadata), stage (`staging/kc-<requestId>.craft`),
dispatch (`EditorLogic.LoadShipFromFile` inside the tracker's dispatch window), settle, plan-versus-loaded verify, a locked grace that ends
once the fingerprint has been stable for 2 s (at most 20 s, then `settleUnstable`), thumbnail settle inside the lock, unlock, terminal.
A failed load or verify reloads the snapshot (`restore.result`); Stop or authority loss after dispatch is `cancelled` with no automatic
restore; a scene change or human input inside the lock is `indeterminate`. After an apply the craft is flagged unsaved under a sentinel
save name so KSP prompts before a human Save overwrites a same-named file; a restore writes the name, description, flag, both save-name
fields and the undo marker back. A craft this service generated and nobody has touched since is not treated as unsaved human work.

`KspControlData/control/` (suspensions) is never reachable from a tool path, and neither is a save literally named `control`.
Staging files go at the terminal state; recovery files stay (newest 50; the latest snapshot of an unsaved craft is never pruned).
Thumbnails KSP writes for staging or recovery names are deleted, a ship-named one is restored from a backup, anything else is only reported.

Not live-verified: that `EditorLogic.LoadShipFromFile` from the staging path leaves the editor in `st_idle` with every part started, the
settle and grace timings, whether the control locks (and the separate `EditorLogic.Lock` id) survive the load, that the unsaved marker
and sentinel produce the stock overwrite prompt, the thumbnail naming (staging path or ship name), that `SaveShip` on an empty editor
still carries `_modVersions`, and the crew and `ShipConstruct.SaveShip` header facts the snapshot records.

## Load (editor_load_craft)

`editor_load_craft(requestId, leaseId, expectedRevision, facility, fileName, expectedSha256, allowUpgrade=false)` loads an existing
`Ships/<facility>/<fileName>` into the editor. It uses the same host path, lease and `editor.replace_craft` grant family as an apply.
The human's file is never loaded in place, never rewritten and never gets a sidecar. In the runner's staging step the bridge:

1. copies the file to `KspControlData/<save>/staging/kc-<requestId>.craft` and hash-checks the copy against `expectedSha256` (`file_changed`);
2. checks link integrity on the text (`craft_invalid_links`), before any KSP code sees the file;
3. loads two independent nodes from the copy and calls `KSPUpgradePipeline.Process(work, copy, LoadContext.Craft, onSuccess, onFail)`
   synchronously on the main thread, never re-entrantly. Without `onSuccess` the job is `craft_upgrade_failed`: the `SaveUpgradeFail`
   popup is dismissed, the `SaveUpgradeFailDialog` lock removed and both callbacks become no-ops;
4. compares the untouched reference with the pipeline output. Any difference (or a changed header `version`) is `upgradedOnLoad`, listed
   in `pipelineDifferences`; without `allowUpgrade` the job is refused `craft_requires_upgrade` with `notDispatched=true`; with it the
   output is staged as `kc-<requestId>.upgraded.craft` and that is loaded (an unchanged craft loads the verified copy);
5. pre-validates the craft to load: parts installed (`craft_parts_missing`, also `AllPartsFound`), and every `MODULE` name present on the
   part prefab (`module_not_installed`).

Then the standard runner: snapshot of a non-empty editor, dispatch, settle, verify (`comparison` = the staged craft against the editor's
own `SaveShip`; reported, never a failure), locked grace, thumbnail settle. After grace the source, its `.original` and its `.loadmeta`
are hashed again: a change to the source or `.original` fails the job `source_changed_during_load` (the editor keeps the loaded copy and
the snapshot stays); a changed `.loadmeta` is reported. KSP's own save-name values are left as the load set them.
`scriptsApplied` is `"unavailable"` unless the game reports it.

## Offline preview packaging

`./KspControl/Build-Package.ps1` builds the bridge and publishes a self-contained
Windows x64 host, then verifies a ZIP allowlist and SHA-256 manifest under a new
`Artifacts/KspControl-preview-*` directory. It refuses tracked source changes or
a source commit change during the build. It does not install, update, launch,
publish or release anything. The GameData payload contains only bridge/contracts
DLLs; it requires the matching LunaMP client facade and existing Newtonsoft.Json
13.0.0.0 assembly. This preview is explicitly unvalidated in live KSP.
