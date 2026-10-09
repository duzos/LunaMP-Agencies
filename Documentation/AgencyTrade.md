# Agency trade

Enable `AgencyTrade` in the server's GeneralSettings.xml and restart with matching clients. It is independent of tooling and vessel-control enforcement. Trade-only servers keep stock launch prices.

Open **Agencies → Trade**. The agency owner creates an offer for one other agency. Include an owned craft in flight, the current editor craft's existing tooling and blueprint, funds or science. Each side can contribute funds and science. Review the terms before sending; creating an offer does not transfer anything.

The receiving agency's owner reviews and accepts or declines. The seller can withdraw an open offer. Acceptance rechecks funds, science, agency owners, craft ownership and the offered design, then transfers everything together. If any check fails, nothing transfers. Open offers expire after 24 hours.

Buying a design copies its tooling license and craft file; the seller keeps its own tooling. Buying a craft transfers that craft's ownership and grants its exact physical-part allowance without automatically buying tooling. Purchased allowances do not unlock technologies or the part catalog. Adding or removing physical parts changes the design; cargo still needs ordinary research and purchase access.

**Received designs** shows file delivery and offers an editor load action. Save existing editor work before confirming replacement. Delivery is acknowledged only after the file is saved, and reconnecting retries missing delivery.

Enable [playtest diagnostics](PlaytestDiagnostics.md) for later troubleshooting. Automated checks do not establish stock/modded craft loading, research UI or in-game trade presentation; those checks remain **NOT RUN**.

## Selling stock (agencies.9)

Choose **Sell stock** as the design mode of a new offer (Trade tab, or **Sell...** on a design in the Designs tab). Enter how many units to sell and attach the craft file. This needs `AgencyTooling` as well as `AgencyTrade`, and you must hold the units; the mode is disabled with "Build stock in the Designs tab first" when you have none. Only the agency owner can sell.

- **Escrow.** The units leave your stock when the offer is created and are held for the offer. They return to you if the offer is declined, withdrawn, expires or becomes invalid. If trade is turned off, stock offers cannot be withdrawn from the client; the escrow returns automatically when the offer expires (24 hours). On accept they move to the buyer along with any funds or science in the offer.
- **No tooling transfer.** You keep your tooling. The buyer gets the units and the craft file (under **Received designs**), not a tooling license, and cannot build more of it.
- **Buyer launches free until empty.** Each launch of that exact design uses one unit. The buyer pays only for inventory, extra fuel and science, the same top-up as the owner. Stock lets the buyer launch parts they have not researched, but only for those launches. When the units run out the buyer must buy more or pay normal prices.
- **Terms travel with the units.** Units keep the price they were built at, so trading never creates funds and recovery stays bounded by what was paid.
- **Limits.** Counted together with launches and open offers: 999 units held per design and 64 stock batches per agency. An accept fails cleanly, and the escrow stays, if the buyer cannot hold the units.
- **Sandbox-built stock cannot be sold into Career.** Units built without funds in Sandbox or Science mode are refused in Career, at create and at accept. Sell stock built in Career instead.

See [Agency tooling](AgencyTooling.md) for building stock, the volume discount and the server settings.
