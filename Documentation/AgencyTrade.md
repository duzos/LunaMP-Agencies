# Agency trade

Enable `AgencyTrade` in the server's GeneralSettings.xml and restart with matching clients. It is independent of tooling and vessel-control enforcement. Trade-only servers keep stock launch prices.

Open **Agencies → Trade**. The agency owner creates an offer for one other agency. Include an owned craft in flight, the current editor craft's existing tooling and blueprint, funds or science. Each side can contribute funds and science. Review the terms before sending; creating an offer does not transfer anything.

The receiving agency's owner reviews and accepts or declines. The seller can withdraw an open offer. Acceptance rechecks funds, science, agency owners, craft ownership and the offered design, then transfers everything together. If any check fails, nothing transfers. Open offers expire after 24 hours.

Buying a design copies its tooling license and craft file; the seller keeps its own tooling. Buying a craft transfers that craft's ownership and grants its exact physical-part allowance without automatically buying tooling. Purchased allowances do not unlock technologies or the part catalog. Adding or removing physical parts changes the design; cargo still needs ordinary research and purchase access.

**Received designs** shows file delivery and offers an editor load action. Save existing editor work before confirming replacement. Delivery is acknowledged only after the file is saved, and reconnecting retries missing delivery.

Enable [playtest diagnostics](PlaytestDiagnostics.md) for later troubleshooting. Automated checks do not establish stock/modded craft loading, research UI or in-game trade presentation; those checks remain **NOT RUN**.
