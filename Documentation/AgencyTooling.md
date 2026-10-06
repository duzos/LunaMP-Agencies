# Agency tooling

Enable `AgencyTooling` in the server's GeneralSettings.xml and restart with matching clients. Missing multiplier keys are added to GeneralSettings.xml on the next server start; an existing ToolingCostMultiplier value is kept, so edit it to change the cost. Default-off tooling uses settings `ToolingCostMultiplier` (5), `TooledLaunchMultiplier` (0.1), `UntooledLaunchMultiplier` (2) and `ToolingCombineMultiplier` (0.1).

The editor's **Agency design** panel shows the current launch price and tooling quote before launch. Untooled craft can still launch, at `UntooledLaunchMultiplier` times the price of their non-science parts. Purchasing tooling charges agency funds once for that exact part multiset: names and counts matter; layout and symmetry do not. Adding or removing a physical part creates a different design. Stored designs are shared by the agency.

Science parts (the Science category in the editor) do not contribute to tooling and remain full price at launch/recovery. Inventory and crew cargo remain full price. Fuel and module prices affect the current quote without changing the physical-part fingerprint.

Combining existing tooled subassemblies charges `ToolingCombineMultiplier` times the full tooling value (current part prices times `ToolingCostMultiplier`) of the parts they replace, plus full tooling for unmatched parts. What the saved design originally cost does not matter, so nested combines cost the same fraction at every level. Counts cannot be reused twice. The quote searches for the cheapest valid combination within bounded complexity; overly complex combinations are refused rather than charged a guessed amount.

The server confirms charges before launch. Interrupted or cancelled unregistered launches refund their reserved charge once; a registered launch is retained after reconnect. Recovery pays according to recorded paid parts, including separated craft, and cannot refund the same parts twice or more than was paid (an untooled launch records its multiplier, so it refunds at most that much). Revert-to-editor is blocked after partial recovery or an external transfer so it cannot undo someone else's assets or issue a duplicate refund.

Set `CanRevert` to `false` in the server's `GameplaySettings.xml` to disable both revert-to-launch and revert-to-editor. The setting controls the stock flight menu and is also enforced by the server before agency refunds or vessel restoration. Restart the server and reconnect clients after changing it. Set it to `true` to allow eligible reverts again.

A successful revert disconnects you so you can reconnect and load the authoritative reverted state. It does not immediately resume the editor or launch scene; this prevents delayed pre-revert packets from overwriting the restored craft.

The client supplies stock/mod cost metadata because the server has no KSP part catalog. This feature validates bounded manifests and arithmetic; it is not server-side price anti-cheat. Gameplay, recovery/revert presentation and editor visual checks remain **NOT RUN**; use [playtest diagnostics](PlaytestDiagnostics.md) for later verification.
