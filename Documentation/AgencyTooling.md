# Agency tooling

Enable `AgencyTooling` in the server's GeneralSettings.xml and restart with matching clients. Missing multiplier keys are added to GeneralSettings.xml on the next server start; an existing ToolingCostMultiplier value is kept, so edit it to change the cost. Default-off tooling uses settings `ToolingCostMultiplier` (5), `TooledLaunchMultiplier` (0.1), `UntooledLaunchMultiplier` (2) and `ToolingCombineMultiplier` (0.1).

The editor's **Agency design** panel shows the current launch price and tooling quote before launch. Untooled craft can still launch, at `UntooledLaunchMultiplier` times the price of their non-science parts. Purchasing tooling charges agency funds once for that exact part multiset: names and counts matter; layout and symmetry do not. Adding or removing a physical part creates a different design. Stored designs are shared by the agency.

Science parts (the Science category in the editor) do not contribute to tooling and remain full price at launch/recovery. Inventory and crew cargo remain full price. Fuel and module prices affect the current quote without changing the physical-part fingerprint.

Combining existing tooled subassemblies charges `ToolingCombineMultiplier` times the full tooling value (current part prices times `ToolingCostMultiplier`) of the parts they replace, plus full tooling for unmatched parts. What the saved design originally cost does not matter, so nested combines cost the same fraction at every level. Counts cannot be reused twice. The quote searches for the cheapest valid combination within bounded complexity; overly complex combinations are refused rather than charged a guessed amount.

The server confirms charges before launch. Interrupted or cancelled unregistered launches refund their reserved charge once; a registered launch is retained after reconnect. Recovery pays according to recorded paid parts, including separated craft, and cannot refund the same parts twice or more than was paid (an untooled launch records its multiplier, so it refunds at most that much). Revert-to-editor is blocked after partial recovery or an external transfer so it cannot undo someone else's assets or issue a duplicate refund.

Set `CanRevert` to `false` in the server's `GameplaySettings.xml` to disable both revert-to-launch and revert-to-editor. The setting controls the stock flight menu and is also enforced by the server before agency refunds or vessel restoration. Restart the server and reconnect clients after changing it. Set it to `true` to allow eligible reverts again.

A successful revert disconnects you so you can reconnect and load the authoritative reverted state. It does not immediately resume the editor or launch scene; this prevents delayed pre-revert packets from overwriting the restored craft.

The client supplies stock/mod cost metadata because the server has no KSP part catalog. This feature validates bounded manifests and arithmetic; it is not server-side price anti-cheat. Gameplay, recovery/revert presentation and editor visual checks remain **NOT RUN**; use [playtest diagnostics](PlaytestDiagnostics.md) for later verification.

## Designs tab, bulk stock and saved craft (agencies.9)

### Designs tab

**Agencies -> Designs** lists every design your agency has tooled. Use the search box to filter by name (case-insensitive substring) and the sort toggle to order by name or by stock. Each design shows its part count, how many stock units are available (plus any units tied up in open offers) and its facility (VAB or SPH). Designs tooled before agencies.9 have no stored name and show a part-based name.

**Build N.** Enter a quantity (1 to 100) and press **Build**. The quote line shows the units, the price each, the total, the volume discount and what a normal tooled launch costs. Confirm to pay: stock is paid up front, cannot be refunded and cannot be un-built. Only a design your agency has tooled can be built; stock you bought cannot be multiplied. Outside Career no funds are charged.

The discount applies to the non-science parts only. Science parts and inventory are always full price. For a build of `n` units:

```
discount(n) = 0                                                            if n = 1
            = StockMaxDiscount * min(1, (n-1) / (StockFullDiscountUnits-1))  if n >= 2
unit price  = science cost + non-science cost * TooledLaunchMultiplier * (1 - discount(n))
```

With the defaults (`StockMaxDiscount` 0.30, `StockFullDiscountUnits` 10) the discount is 0%, 3.3%, 6.7% ... 26.7% for 1 to 9 units and 30% from 10 units up. Each build is priced on its own size, so building 10 at once is cheaper than two builds of 5. The total always rises with the quantity.

**Load into editor.** **Load** opens the saved craft for a design in the VAB or SPH with no craft browser. It works from the Space Center or an editor scene, asks before replacing a craft that is already open, and writes a normal copy to `Ships/VAB` or `Ships/SPH` named `Tooled-<name>-<id>.craft`. It refuses if the craft uses parts that are not installed. Load is disabled when no craft is saved for the design.

**Save craft to tooling.** In the editor's **Agency design** panel, a design that is already tooled shows **Save craft to tooling (free)** when no craft is stored for it or the stored one differs from the open craft. Tooling a new design saves the craft automatically. Saving is free and replaces the stored craft. It can be skipped for a minute after the last save or when a storage limit is reached; the tooling purchase itself is never blocked by storage.

### Launching from stock

When the open craft matches a design you hold stock for, the panel shows **Using 1 of N stock** and what you pay. The unit covers the non-science part cost already paid at build time. You still pay for anything that makes the launch cost more than that prepayment: extra fuel, inventory and crew cargo, and science parts at full price. A **Use stock** toggle lets you keep the stock and pay the normal tooled price instead. Stock is spent when the launch is prepared. It comes back if the launch is cancelled, times out or you disconnect, and on a revert-to-editor (not on revert-to-launch). A stock launch also lets a buyer fly parts they have not researched, for that launch only.

Stock keeps the terms it was built at. Changing `StockMaxDiscount` or `StockFullDiscountUnits` later does not reprice stock you already hold, and recovery never refunds more than was paid. If all 64 batch slots are full when you revert a stock launch and no batch has matching terms, the unit is refunded as its prepaid price instead of being returned (0 for Sandbox-built stock).

### Limits

- 100 units per build.
- 999 units held per design per agency. Units in open offers and in launches that can still be reverted count toward it.
- 64 stock batches per agency. Batches built or bought on identical terms merge, so this rarely matters. Batches in open offers and prepared launches count too. If you hit it, launch or sell some stock first.
- Saved craft: 512 KB each, 8 MB per agency, 256 MB per server.
- Stock built in Sandbox or Science mode (no funds charged) carries no prepayment. It can be launched in Career at a reduced price but cannot be sold into Career (see [Agency trade](AgencyTrade.md)).
- Requires `AgencyTooling`. If tooling is turned off, existing stock is kept but cannot be built or launched.

### Server settings

| Setting (GeneralSettings.xml) | Default | Meaning |
|---|---|---|
| `StockMaxDiscount` | 0.3 | Largest volume discount on the non-science share. At least 0 and below 0.5. |
| `StockFullDiscountUnits` | 10 | Units in one build that reach the full discount. 2 to 1000. |

Invalid values are logged at startup and both settings fall back to 0.3 and 10 in memory; the file is not rewritten. Clients receive the effective values, so the Designs tab and the server quote the same price. Missing keys are added on the next start. Clients and server must run the same build.

### Rolling back to agencies.8

agencies.9 stores its economy as version 3 (`Universe/AgencyEconomy.json`), which agencies.8 refuses to load. Do not just restore an old backup: that erases everything since the upgrade and can recreate spent funds. Use the downgrade tool.

1. Stop the server cleanly, so no vessel journal is pending.
2. Back up the whole `Universe` folder. This is the last resort if anything below goes wrong.
3. Run `Server --downgrade-agency-economy` (add the data directory option if the server does not use the default). It runs without starting the server and exits 0 on success or 1 on refusal, changing nothing on refusal.
4. Read the summary. The tool first writes `Universe/AgencyEconomy.v3.<utc>Z.bak.json`, then converts the file to version 2 atomically.
5. Start the agencies.8 server build.

What it does: prepared launches are refunded; open offers are cancelled and escrowed stock is returned; every stock unit is refunded to its agency at its prepaid price (Sandbox-built units refund 0); stock, stock offers, stock craft entitlements and saved tooling craft references are removed. Registered launches that used stock keep their recorded charge, so an agencies.8 revert refunds only the top-up. `Universe/AgencyBlueprints/` is left in place and ignored by agencies.8, and the `Stock*` settings stay in GeneralSettings.xml and are ignored. To clear the blueprint folder, delete only files named by 64 hex characters plus `.craft` or `.craft.tmp`.

Gameplay, editor loading and trade presentation of these features remain **NOT RUN** until the guided playtest.
