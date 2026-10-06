# Agency CommNet agreements

Add `<AgencyCommNetOptIn>true</AgencyCommNetOptIn>` and `<AgencyCommNetPerAgency>true</AgencyCommNetPerAgency>` to the server's existing GeneralSettings.xml, then restart with matching clients. Opt-in defaults off. Vessel ownership enforcement may remain off.

Open **Agencies → CommNet**. Choose one of your agency's craft, then enable a foreign craft in its connection list. The foreign craft must also choose yours or enable **Accept all foreign craft**. Both sides must agree. Your own agency's craft always link; stock ground stations retain their normal behavior. Agreements permit links but do not change antenna range, power or occlusion.

The panel lists craft and owning agencies, shows which side still needs to agree, and displays server-confirmed changes. Any member of the owning agency can edit that craft's agreements. Co-owner flight access does not grant relay-policy management.

Ownership/access changes invalidate affected agreements, including choices made by other craft toward the changed craft. A handover back to the original owner does not revive old permissions. Newly separated craft start without agreements. Reconnect reloads saved settings. An unavailable settings store blocks foreign links and edits rather than treating missing data as permission.

The installed KSP API is inspected for the connection hook; the panel reports a hook-attachment failure. Enable the diagnostics in [PlaytestDiagnostics.md](PlaytestDiagnostics.md) when playtesting. Automated tests cover policy, persistence and network messages. Actual routing, signal strength and rendered UI checks remain **NOT RUN**.
