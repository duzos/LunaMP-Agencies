# Zero starting kerbals

To give newly created agencies an empty crew roster, enable both settings in the server data directory's `Config/GeneralSettings.xml` and restart the server:

```xml
<AgencyKerbalsPerAgency>true</AgencyKerbalsPerAgency>
<AgencyZeroStartingKerbals>true</AgencyZeroStartingKerbals>
```

The new setting defaults to `false`. Add it explicitly when enabling the feature in an existing configuration; preserve your other settings instead of replacing the file. When per-agency rosters are disabled, the zero-starting setting has no effect.

Both newly created solo agencies and named agencies initialize their roster at creation. With both flags enabled, this roster is empty. With per-agency mode enabled and zero starting kerbals disabled, new agencies receive the four canonical starters. The stock Astronaut Complex remains the hiring interface and stock hiring prices apply; this feature does not add a UI or change hiring costs.

Existing roster directories are authoritative, including empty ones. No crew is removed, replaced or automatically restored on later requests or flag changes. Existing agencies without a private roster keep the existing startup migration from the global crew roster; if that is unavailable, their first roster access creates the canonical starters regardless of the zero-starting flag. Agencies created while per-agency mode was disabled follow that same legacy migration path.

If verbose diagnostics are enabled, `kerbal.initialize` reports agency ID, initialization reason, crew count and both flags. `kerbal.migration` identifies legacy global copies; startup snapshots include `zeroStartingKerbals`.

Automated coverage verifies policy combinations, directory and legacy migration behaviour, protocol delivery and recruit persistence. In-game UI, actual stock hiring and hiring-cost checks remain **NOT RUN** until a user playtest. For that check, create a fresh agency, confirm no starting astronauts, hire through the stock Astronaut Complex, then reconnect and confirm the hire remains in that agency only.
