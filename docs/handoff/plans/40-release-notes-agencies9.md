# agencies.9 (v0.30.0-agencies.9)

Bulk-built design stock: build N tooled craft at a volume discount, launch them cheaply and sell them to other agencies.

- New **Designs** tab: search and sort your tooled designs, see stock, **Build N** at a volume discount, **Load** a design straight into the VAB or SPH.
- The volume discount defaults to 30% off the non-science parts at 10 units or more (0% for 1 unit). Server settings `StockMaxDiscount` and `StockFullDiscountUnits` change it.
- Launching a craft you hold stock for shows "Using 1 of N stock". You still pay for extra fuel, inventory and science. A **Use stock** toggle keeps the stock and pays the normal price.
- New **Sell stock** trade mode: units are held in escrow, no tooling transfers, and the buyer launches free until the stock is empty.
- **Save craft to tooling (free)** in the editor stores the craft for Load. New tooling saves it automatically.
- Limits: 100 units per build, 999 held per design, 64 stock batches per agency (offers and launches count).
- Stock built in Sandbox or Science cannot be sold into Career.
- Stock keeps the price it was built at, even if the server settings change later.
- Reverting a stock launch with all 64 batches full refunds the unit's prepaid price instead of returning the unit.
- Server: the economy file is now version 3. Saved craft live in `Universe/AgencyBlueprints/` as hash-named `.craft` files.

Client and server must use the same build.

**Rollback warning:** agencies.8 cannot read the new economy file (version 3). To go back, stop the server, back up the `Universe` folder, run `Server --downgrade-agency-economy`, then start agencies.8. Held stock is refunded at its prepaid price and open stock offers are cancelled. Do not simply restore an old backup. Details are in `Documentation/AgencyTooling.md`.
