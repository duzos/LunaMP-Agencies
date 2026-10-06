# Agency vessel ownership and docking

Add `<AgencyVesselOwnership>true</AgencyVesselOwnership>` to the server's existing `GeneralSettings.xml`, then restart with matching updated clients. The default is false; existing configuration files do not gain the key automatically.

## Ownership

A newly accepted craft belongs to its launching agency. Existing recorded owners are preserved. Existing craft without an owner stay ownerless until an agency member claims them. Any member of the owning agency or a co-owner agency can fly a craft; everyone else spectates. Forced control requests cannot bypass these permissions.

Open **Agencies → Vessels** to find a craft by name or agency. The panel shows ownership and flight access. Agency owners can add/remove co-owner agencies, set an offline docking policy or hand over a craft. Handover requires confirmation, removes existing co-owners and resets offline docking to Nobody. Changes only appear after the server confirms them. Mixed-owner docked assemblies must be separated before handover.

## Docking and claws

Docking within your agency or with an ownerless target is allowed. When a foreign craft's owning agency is online, its members receive a request to allow one dock or decline. This also applies to requests from co-owner agencies. When that agency is offline, the target's configured policy applies: Nobody, Co-owners or Anyone. An unanswered online request expires; it does not silently become offline permission.

Wait for permission, then continue your approach. Stock geometry and speed conditions still determine capture. Permission has an expiry and cannot be reused. Ownership changes and disconnects invalidate outstanding permission. Claws use the same consent rules. Normal EVA seat operations are separate.

The combined craft keeps the surviving vessel's owner. The server retains the ownership of its original constituents so undocking into a new vessel ID restores the separated craft's owner. Newly created debris inherits the relevant parent ownership. Co-owner changes affect that owner's constituents, not visiting craft.

During completion, vessel updates pause briefly until the server confirms the joined craft. If completion cannot be confirmed, the client reconnects and reloads the authoritative state. The server journals accepted coupling data before removing the weak vessel, so an interrupted acknowledgement does not discard its parts.

## Persistence and diagnostics

The registry is stored in `Universe/AgencyVesselOwnership.json`. The request and grant lifetimes use `AgencyDockRequestTimeoutSeconds` and `AgencyDockGrantTimeoutSeconds` (bounded to 1-120 seconds).

The richer ownership registry migrates the existing vessel-to-agency text map without reassigning recorded owners. Ownership, permissions and docked constituent provenance share one authoritative store. An unreadable registry blocks ownership mutations instead of silently replacing it. Coupling recovery runs before clients are admitted after restart.

Enable the opt-in client and server logging described in [PlaytestDiagnostics.md](PlaytestDiagnostics.md) when playtesting. Send both logs, the craft and agency names, and the attempted action. Useful cases include unauthorized control, co-owner revocation, handover, online consent, offline policy, claw capture and undocking a foreign visitor.

Automated protocol/server tests and installed KSP API inspection do not prove physical docking, claw motion or rendered UI. Those in-game checks remain **NOT RUN**.
