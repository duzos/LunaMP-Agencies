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

Current tools are `editor_state`, `editor_engineering`, `craft_plan`, `editor_apply_craft`, `editor_restore_snapshot`, `editor_save_craft`, `craft_list`, `job_status`, `control_status`, `control_acquire_lease`, `control_renew_lease`, `control_release_lease`, `capabilities`, `context`, `parts`, `editor`, `vessel`,
`part_controls`, `science`, `part_definition`, `editor_snapshot` and the six flight tools (see Flight). `part_definition` and `editor_snapshot` provide bounded configured-part and native editor snapshots; they do not import or create craft. Part and vessel pages are bounded. Part controls
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
KspControl.Host.exe grant issue --ksp-root <KSP root> --save <SaveFolder> [--agency <guid>] [--ops a,b] [--facilities VAB] [--policy refuse|snapshot_then_replace] [--max-parts N] [--hours H] [--spend FUNDS]
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

## Save (editor_save_craft, craft_list)

`editor_save_craft(requestId, leaseId, expectedRevision, fileName, replaceExpectedSha256?)` writes the editor craft as a ship file in
`saves/<Save>/Ships/<facility>` (the editor's facility; a VAB grant covers only VAB). It needs a held lease, a grant that lists
`craft.write` (recipient `ships:<facility>`, separate from the `editor.*` family) and a fresh `editorRevision`. `fileName` is a bare name
(1..64 of `A-Z a-z 0-9 space . _ -`, no leading dot, no `..`, no `.craft` suffix); the extension is implied. Same host path and job
envelope as apply (`requestId` dedupe, journal, `job_status`).

Admission refuses before any write: `craft_empty`, `path_outside_save` (outside Ships, a reparse point anywhere up to `saves`, a save
named `control` has no ledger), `ledger_unavailable` (a corrupt `ledger.json` is never overwritten or guessed at), and the overwrite rules.
The runner decides the same thing again in the step that writes. That step runs under the operation lock and does, in one frame:

1. capture the live craft (`SaveShip`) with the header `ship`, `description` and `missionFlag` taken from the editor fields, the UI field
   itself untouched; refuse `craft_identifiers_invalid` unless every `PART.part` ends in a unique unsigned integer (the
   `ToolingClient.CraftIndices` rule), `craft_too_large` over 2 MiB;
2. create-only write (`<target>.kspcontrol.<guid>.tmp` then `File.Move`, which never overwrites) or, for a replace, `File.Replace`;
3. read the file back and check: hash, parse, identifier rule, the comparator against the capture (`comparison`, equal expected, zero
   unregistered differences) and `ShipConstruction.AllPartsFound`. A failure removes the new file (or puts the replaced bytes back) and
   returns `save_verify_failed` (`indeterminate` if even the revert fails);
4. record ownership in `KspControlData/<save>/ledger.json` (entries for files that no longer exist are pruned; a failed ledger write is
   reported as `ledger: write_failed` and leaves the file saved but never replaceable);
5. set `vesselNameAtLastSave` and `vesselNameAtLastSave_Sanitized` to the base name written and `undoIndexAtLastSave = undoLevel`
   (`saveBookkeeping: synced | partial | write_failed | unavailable`), so a later human Save of that same file proceeds without a prompt
   and a human Save to any other existing file still prompts.

**Overwrite rule.** An existing file is never overwritten: `file_exists` without `replaceExpectedSha256`. With it, the file must be in the
ledger (`file_not_kspcontrol_owned` otherwise), its current hash must equal `replaceExpectedSha256` (`file_changed`) and the hash the
ledger recorded (`file_changed`, "modified since KspControl wrote it"). A human file is never replaced, whatever hash is supplied. Names
compare case-insensitively (Windows).

Declared outputs: the `.craft` (`created` or `replaced`), the ledger, and anything else that appears or changes in the Ships folder
during the job (`.craft.original`, `.loadmeta`), which is reported and never a failure. No thumbnail is captured.

`craft_list(facility, offset, limit, filter)` is read-only and needs no lease: `{fileName, sizeBytes, modifiedUtc, sha256, kspControlOwned}`
for the `.craft` files of the current save, name-ordered and paged (at most 50, hashed per page). `kspControlOwned` is true only when the
ledger lists the file and its current hash equals the recorded one. Names the tools cannot address and linked files are counted, not listed.

**Not in this slice.** `editor_verify_roundtrip` is not a tool: R1-section 4 and every later revision dropped it. The roundtrip gate
(save, then `editor_load_craft` of the saved file with a comparison) belongs to the load slice (P2.9); this slice's file-level
comparison is the part that can be checked without loading. The LunaMP `FromFile` versus `Build(ship)` fingerprint check needs the facade
v2 members that R1-section 12 deferred to P3; the pure `CraftIndices` rule is checked here.

Not live-verified: that a file written this way loads through the craft browser and `LoadShipFromFile` with no `.craft.original`, that the
save-name fields and marker produce no prompt for a human Save of the same file and a prompt for another existing one, that
`File.Replace` and `File.Move` behave the same under KSP's Mono as under .NET, and the sidecars and `.loadmeta` timing beside a save.

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
the snapshot stays); a changed `.loadmeta` is reported. An overwrite guard (sentinel save name and unsaved marker) is written after every load, so KSP prompts before a human Save overwrites the source.
`scriptsApplied` is `"unavailable"` unless the game reports it.
## Flight (flight_state, flight_set_controls, flight_stage, flight_action_group, flight_abort, flight_warp)

`flight_state` is read-only and needs no lease. It answers only for an active vessel the agency owns (facade v1; otherwise
`owned_active_vessel_unavailable`) and reports situation, body, UT, altitude, vertical/surface/orbital speed, the orbit
(`referenceBody` is the sphere of influence the vessel is in now, the proof for a Mun encounter; `predictedNextBody` is a forecast only),
stage number with per-stage delta-V from the stock `VesselDeltaV`, resource totals, throttle, SAS/RCS/gear/lights/brakes, warp, crew count and
MechJeb presence. Units are in the field names; stages and resources are capped at 32.

The other five tools mutate. They need a held lease and a grant that lists the `flight.control` family on the `FLIGHT` facility
(`grant issue ... --ops flight.control --facilities FLIGHT`; add `VAB` for an editor grant in the same file). The lease binds to
`vessel:<guid>` of the active vessel, so a vessel switch or scene change revokes it. Each request runs in one main-thread drain: parse, lease,
classify every effect, admit, check preconditions, then revalidate (`ValidateForDispatch`) immediately before each callback, dispatch, and read
the state back. The envelope reports `applied`, `consequential`, `observed` and `notDispatched`; a callback whose effect the game does not
confirm is `indeterminate` and the request id stays reserved (the bridge and the host journal both answer a retry without acting again).

- `flight_set_controls`: `throttle` 0..1, `throttleDelta` -1..1, and desired states for `sas`, `rcs`, `gear`, `lights`, `brakes` (setters, never blind toggles; a matching state is not touched).
- `flight_stage(expectedStage)`: activates the next stage only when the vessel is at `expectedStage`; the modules on that stage's parts are classified first.
- `flight_action_group(group, state?)`: Gear, Light, Brakes, SAS, RCS, Custom01..10. With `state` the group is reconciled to it; without, it is toggled once and read back.
- `flight_abort`: fires the Abort group; classified like a group, throttle untouched.
- `flight_warp(rateIndex)`: through the stock `TimeWarp.SetRate`, so LunaMP's Harmony prefix still applies the server's warp rules (`warp_denied` when it refuses). Capped at an effective 1000x (rails) and 1x (physics), by the altitude limit, and refused while the throttle is above zero.

Classification: every part action a stage or group would trigger is named by its module. Known benign modules (lights, gear, brakes, panels,
animations) and known consequential ones (engines, RCS, decouplers, fairings, docking, parachutes, clamps, science, robotics; reported in
`consequential[]`) pass. A module in neither table, including any mod action, makes the whole request `unclassified_effect` before any callback
runs, and its effect (`flight.unclassified`) is not in the authority's effect map either. Add modules to `FlightEffectClassifier` deliberately.

Control ownership: `FlightControlGuard` owns the one fly-by-wire callback (`Vessel.OnFlyByWire`) only while a lease is held. Stop (hotkey or
panel), expiry, the heartbeat watchdog, a context change, a failed grant and `OnDestroy` all end it: the throttle is zeroed first, then the
callback removed, and a warp the bridge raised returns to real time. An agent `control_release_lease` and a human takeover only remove the
callback and keep the throttle (the human's controls are preserved). A player holding a stick, throttle or stage key for three frames takes
the lease over. The authority raises `LeaseEnded` on whatever thread ended the lease; the guard only sets a flag there and does the work on the
main thread the same frame (Stop does it immediately).

Not live-verified: the `Vessel.OnFlyByWire` throttle write persisting across physics ticks, `ActionGroupList.SetGroup` firing the part actions for
Gear/Light/Brakes/RCS/Abort (versus toggling only), `StageManager.CurrentStage` advancing within the frame of `ActivateNextStage`, the stage
mass unit of `DeltaVStageInfo.stageMass`, `GameSettings` key polling, and LunaMP's veto of `TimeWarp.SetRate` against a real server.

## Launch (editor_launch)

`editor_launch(requestId, leaseId, expectedRevision, launchSite, maxSpendFunds)` launches the editor craft through the normal editor path:
the LunaMP facade (`ControlObservation`, `ApiVersion` 2) selects the site and runs `EditorLogic`'s own launch routine, so stock pre-flight, the
crew manifest and the agency tooling reservation (`ToolingClient.BeginLaunch`, `PrepareLaunch`, `RegisterLaunch`, `CancelLaunch`) stay in charge.
Nothing here uses `AssembleForLaunch` or writes a balance.

A human arms it: `grant issue ... --ops launch --spend <funds>` (`launch` is the family name of the `editor.launch` effect; the spend limit is
signed into the grant, defaults to 0 and is never raised by retries or income). The host journal keeps the cap per grant id:
gross charged funds plus every held reservation plus the new `maxSpendFunds` must stay within `spendLimitFunds`, else `spend_cap_exceeded`
before the bridge is asked. A completed launch records the charge the bridge confirmed (tooling result, or the quote if none) and releases the
rest; a cancelled or failed one releases it all; an indeterminate one keeps it held.

Bridge job: admission quotes the craft (`launchCost` above `maxSpendFunds` is `spend_exceeds_max`), checks the launch allowance
(`AgencyTradeResearch.ValidateLive`), no pending reservation and a confirmed balance; then `begin_launch` invokes the editor routine and
`await_flight` waits for the reservation, the FLIGHT scene, a new pad vessel with an ownership record of this agency, and `LaunchPending`
false (registered). Success returns `vesselId`, `charge`, `chargeSource`, and `leaseContinues`: the lease moves to `vessel:<guid>` only if
the grant lists a `flight.*` family (which the grant maps only with the `FLIGHT` facility), otherwise it is released. An abort before the flight scene loads (authority lost, Stop, timeout, server
refusal) is cancelled through the existing cancel command and ends only once the refund shows in the confirmed balance; a flight scene that is already
loading, a disconnect, or an unconfirmed refund ends `indeterminate`.

Not live-verified: that `EditorDriver.setLaunchSite` plus `EditorLogic.launchVessel` is the exact routine the Launch button runs in this KSP
build (and which of the two carries the site), what a failing stock pre-flight check does to a launch started this way (a prompt would end the job
`launch_not_started`), the crew dialog for crewed craft, that `RegisterLaunch` clears `LaunchPending` before the ownership record arrives, and the
refund timing after a cancel.

## Offline preview packaging

`./KspControl/Build-Package.ps1` builds the bridge and publishes a self-contained
Windows x64 host, then verifies a ZIP allowlist and SHA-256 manifest under a new
`Artifacts/KspControl-preview-*` directory. It refuses tracked source changes or
a source commit change during the build. It does not install, update, launch,
publish or release anything. The GameData payload contains only bridge/contracts
DLLs; it requires the matching LunaMP client facade and existing Newtonsoft.Json
13.0.0.0 assembly. This preview is explicitly unvalidated in live KSP.

## MechJeb autopilot (P3b)

Tools: `mechjeb_status` (read-only, no lease), `mechjeb_ascent`, `mechjeb_execute_node`, `mechjeb_plan_circularize`, `mechjeb_plan_hohmann_to_target`.
Every mutation needs a lease taken in the flight scene and a grant that lists `flight.autopilot` with the `FLIGHT` facility: the facility maps to the entity
`vessel:*`, the lease entity is `vessel:<guid>` of the active vessel (a vessel switch revokes it), and admission also checks that the lease vessel is the active one.
The host journal entity is `flight:vessel`. The family is not in the CLI's default operations: `grant issue --ops flight.autopilot --facilities FLIGHT`.
`GrantCli.AllowedOperations` lists the operations a grant may name; an unknown name is rejected. Jobs go through the same host journal as the editor
mutations and are followed with `job_status` (the host polls `flight.autopilot_status` for them).

**Adapter.** `MechJebAdapter` is guarded reflection with no compile-time reference. It resolves `MuMech.MechJebCore` and the module types once
(capability flags: installed, version 2.15.x supported, per-module), then re-reads the live objects every call: nothing is cached across frames. Members used:
`core.MasterMechJeb`, `core.Ascent` (`MechJebModuleAscentBaseAutopilot`: `Status`), `core.AscentSettings` (`DesiredOrbitAltitude.Val`, `DesiredInclination.Val`,
`Autostage`, `SkipCircularization`, `AscentType`), `core.Node` (`ExecuteOneNode(object)`, `ExecuteAllNodes(object)`, `Abort()`, `State`, `Autowarp`,
`NextNodeBurnTime()`), `core.Thrust.ThrustOff()`, `core.Landing`/`Airplane`/`Attitude`/`Rover` and `GetComputerModule<T>()` for rendezvous, docking and spaceplane,
and each module's `Enabled` and `Users` (`UserPool.Add`/`Remove`). The user is a bridge-owned object, so the adapter can tell its hold from anyone else's.

**Ascent job.** Writes the settings and reads them back (a value that did not take fails with `engage_failed`), adds the user, and reports phase, MechJeb's status
text and altitude, apoapsis and periapsis each frame. Orbit means periapsis above the atmosphere top of the current body for one second; the job then waits for
MechJeb to end its own circularization (up to 2 minutes) and completes with `ascentFinished` true or false. Timeout is 20 minutes; `autoWarp=true` is refused
(`unsupported_option`): MechJeb 2.15 has no warp setting for the ascent. **Node job.** `Autowarp` is forced off; ends when the node is consumed (60 minute timeout).

**Safety.** Admission refuses with `competing_controller` if any other MechJeb autopilot or support module has a user or AtmosphereAutopilot has an active
module (an unreadable AtmosphereAutopilot fails closed). Each frame the runner revalidates the authority, the vessel id and the controls before it reads MechJeb:
Every MechJeb, AtmosphereAutopilot and throttle call names the vessel the job was admitted on (never whichever is active now); the throttle of the live input state is only
touched while that vessel is still active. A user counts as ours if it is the bridge user, the ascent window module the ascent is engaged through (so MechJeb's own
Disengage button stops it; switching it off before orbit ends the job cancelled as `ascent_disengaged`, not a takeover; the window counts as ours for ascent jobs only, so a person engaging it during a node burn is a competitor), or a MechJeb module whose user set contains ours, which is how the ascent hands over to the node
executor and attitude controller. Attitude, thrust and rover are scanned for foreign users on every pass. The node executor is aborted only when nobody else holds it, and
its Autowarp is restored on release. An orbit needs periapsis above the higher of the atmosphere top and the body's safe altitude (`minOrbitalDistance - radius`), and a target at
below the higher of the atmosphere top and the safe altitude plus 5 km is refused; when MechJeb has ended, the orbit is judged at once.
Stop (button, hotkey, same call) and any loss of authority remove the user, abort the executor and cut the throttle at once; a switch of vessel or scene ends the job;
two consecutive frames of flight-control input (keys or axes), another user entering the module, or another controller engaging is a human takeover
(`human_input_during_operation`, `ExecutionAuthority.HumanTakeover`, lease revoked, 30 s cooldown, throttle cut).

**Planning.** `mechjeb_plan_circularize` creates a stock maneuver node at the apoapsis from vis-viva arithmetic (`plan.source` is `stock_math`).
`mechjeb_plan_hohmann_to_target` creates the departure-burn node for a moon of the current body at the next phase-angle window, assuming near-circular
coplanar orbits, and is labelled an ESTIMATE (`hohmann_phase_wait_estimate`). MechJeb's `Operation*` planner classes exist in 2.15.2 (`OperationCircularize`,
`OperationInterplanetaryTransfer`, ...) but their time-selector behaviour is not verified, so the planner path is not used; they are listed in
`mechjeb_status.plannerOperations`.

Not live-verified: that `UserPool.Add` enables the module (the adapter sets `Enabled` itself if not), what `core.Ascent` returns for each `AscentType` (PVG is
untried), the `Status` strings, that the node executor disables itself after the last node, the human-input key and axis list, AtmosphereAutopilot's
`getVesselModules`/`Active` semantics, the node delta-v frame (radial, normal, prograde in `ManeuverNode.DeltaV`), the Hohmann lead-angle sign, and that
`FlightGlobals.ready` is the right flight-scene readiness test.
