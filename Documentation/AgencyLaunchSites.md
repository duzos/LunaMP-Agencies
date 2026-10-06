# Agency launch sites

Enable this setting inside the server data directory's `Config/GeneralSettings.xml`, then restart:

```xml
<AgencyLaunchSitesPerAgency>true</AgencyLaunchSitesPerAgency>
```

It defaults to `false`. Add the key to an existing configuration without replacing the other settings. Assignments are retained while enforcement is off, so admins can prepare them first.

Each site belongs to one agency. Assigning it elsewhere moves ownership immediately. **An agency with no sites cannot launch.** `LaunchPad` and `Runway` require assignments too; there is no KSC fallback. Existing stock difficulty, discovery and DLC requirements still apply.

## Admin controls

Open the admin window's **Launch sites** tab and enter the admin password. Select an agency, search the site list, then assign a site. Moving or unassigning a site asks for confirmation and warns when its former owner would have no sites left. The list shows authoritative server ownership; a button click does not change ownership locally.

The catalog contains the stock, Making History and Kerbal Konstructs sites loaded in the admin's client. Refresh it after the space centre loads. Saved assignments for sites that are not installed locally remain visible and can be unassigned. Use the exact site identifier displayed by the client. The server does not have a catalog of installed mod sites.

Console equivalents:

```text
assignlaunchsite "Kerbin Dynamics" "LaunchPad"
assignlaunchsite "Kerbin Dynamics" "A Modded Site With Spaces"
unassignlaunchsite "Kerbin Dynamics" "LaunchPad"
listlaunchsites
listlaunchsites "Kerbin Dynamics"
```

Agencies can be identified by name, full ID, or an unambiguous ID prefix. Quote names and site IDs containing spaces. Unassigning requires the specified agency to remain the owner, protecting against stale commands. Remote admin actions require the configured server admin password.

Assignments are stored in `Universe/AgencyLaunchSites.json`. Changes are saved before being announced as successful. Startup and assignment changes warn about agencies without launch access. The agency window's **Mine** tab shows that agency's allowed sites.

## Kerbal Konstructs

Assignments replace KK's paid opening and closing of launch sites. Assigned sites are treated as open; other sites are closed. The paid open/close controls show an assignment notice instead of charging or refunding funds. Other KK facility transactions are unchanged. The assignment overlay is excluded from KK career saving, preserving normal KK state when the feature is disabled.

KK support is attached dynamically. Unsupported method signatures or purchase-button patterns produce diagnostics and prevent KK launch access while this feature is enabled. They do not abort LMP startup. Stock launch-site enforcement remains separate. Physical destruction repair is not part of assignment management.

## Verification and diagnostics

Use updated clients with the updated server. Enforcement is client-side, as intended; this is not an anti-cheat boundary. Older clients may ignore the new settings and assignments.

Automated tests cover assignment policy, persistence, command parsing, admin authentication, network sync, protocol compatibility and the KK purchase-pattern matcher. Installed KSP/KK assemblies and pinned KK source were inspected to verify hook targets. These checks do not prove rendered UI or actual launch behaviour.

In-game stock, Making History and KK launch/selector checks remain **NOT RUN**. For a future playtest, enable client and server verbose diagnostics as described in [PlaytestDiagnostics.md](PlaytestDiagnostics.md), then check assignment, reassignment, no-site denial, reconnect, and feature-off restoration. Send the client and server logs together, including the attempted site and agency.
