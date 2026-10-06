# Craft visibility

Enable `AgencyHideCraft` in GeneralSettings.xml and restart with matching clients. It is independent of vessel control, tooling and trade. The server still sends every craft; hiding is a client presentation feature, not an anti-cheat boundary.

Open **Agencies → Visibility**. Your agency owner can share all agency craft with selected agencies. In **Individual craft**, choose a craft and set each recipient to **Inherit**, **Share**, or **Private**. Inherit follows agency-wide sharing. Share reveals the craft at every distance. Private removes explicit sharing for that craft, but does not prevent range detection. Ownership changes invalidate old craft overrides.

Own-agency craft remain visible. Other craft appear while shared or inside sensor or physics range of any craft owned by your agency. Sensor radius, in metres, is the strongest available individual antenna power multiplied by the game's CommNet range modifier. This is the space-race detection rule, not the stock communication range between two antenna endpoints. Deployment, availability and unloaded antenna state are taken from KSP's antenna interface.

Physics reveal has a 2,500 m minimum and respects larger current pack/unpack ranges. Craft without available antennas only reveal through that physics range. Visibility is refreshed in bounded batches; range-only visibility expires when detection is lost. Debris and EVA follow the same ownership and range rules.

Hidden craft are excluded from map icons and orbits, tracking lists, flight labels, KSC markers, targeting and spectator selection. Losing visibility while spectating exits safely to the space centre. Disabling the feature restores display state owned by the hiding adapter and retains stock filter choices.

Enable [playtest diagnostics](PlaytestDiagnostics.md) to capture visibility decisions and hook availability. Map, tracking, label, spectator and restoration behavior remains **NOT RUN in-game** until the guided playtest. A successful build and policy tests do not prove visual completeness.
