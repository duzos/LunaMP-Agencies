using System;
using System.Reflection;
using HarmonyLib;
using LmpClient.Harmony;
using LmpCommon.Enums;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// Fail-closed disclosure boundary for the optional mission-control bridge. Version 2 adds read-only quote, allowance and balance
    /// reads plus one launch entry that runs the editor's own launch path. The bridge reaches this type by reflection only.
    /// </summary>
    public static class ControlObservation
    {
        public const int ApiVersion = 2;
        public static Guid AgencyId => MainSystem.NetworkState >= ClientState.Connected
            ? AgencySystem.Singleton.MyAgencyId : Guid.Empty;

        public static bool MayInspectActiveVessel(Guid vesselId)
        {
            var agency = AgencySystem.Singleton;
            if (AgencyId == Guid.Empty || !agency.OwnershipReady) return false;
            var records = agency.GetOwnershipSnapshot();
            return records.TryGetValue(vesselId, out var record) && record.OwnerAgencyId == AgencyId;
        }

        /// <summary>True from the moment the editor launch asked the server to reserve funds until the vessel is registered or the launch is cancelled.</summary>
        public static bool LaunchPending => ToolingClient.LaunchPending;

        /// <summary>The confirmed, server-authoritative agency balance, or NaN while it is not known.</summary>
        public static double AgencyFunds => ToolingClient.TryConfirmedFunds(out var funds) ? funds : double.NaN;

        /// <summary>The launch charge of the latest confirmed server reservation (NaN if none) and a counter that grows with each one.</summary>
        public static double LastLaunchCharge => ToolingClient.LaunchChargeSerial > 0 ? ToolingClient.LastLaunchCharge : double.NaN;
        public static long LaunchChargeSerial => ToolingClient.LaunchChargeSerial;

        /// <summary>The latest tooling status line (why a reservation was refused, for example).</summary>
        public static string LaunchStatus => ToolingClient.LatestStatus;

        /// <summary>
        /// The agency quote for the craft now in the editor: what launching it costs, what tooling it would cost, whether its design is
        /// already tooled, and the fingerprint of the priced manifest. Without agency tooling the stock ship cost is reported instead.
        /// </summary>
        public static bool TryQuoteEditorCraft(out double launchCost, out double toolingCost, out bool alreadyTooled, out string fingerprint, out string reason)
        {
            launchCost = 0; toolingCost = 0; alreadyTooled = false; fingerprint = null; reason = null;
            try
            {
                var ship = HighLogic.LoadedSceneIsEditor ? EditorLogic.fetch?.ship : null;
                if (ship == null || ship.Parts.Count == 0) { reason = "editor_empty"; return false; }
                if (ToolingClient.Enabled)
                {
                    var quote = ToolingClient.DisplayQuote(ship, null, ShipConstruction.ShipManifest);
                    if (quote == null) { reason = "economy_not_ready"; return false; }
                    if (!quote.Success) { reason = string.IsNullOrEmpty(quote.Reason) ? "quote_failed" : quote.Reason; return false; }
                    launchCost = quote.LaunchCost; toolingCost = quote.ToolingCost; alreadyTooled = quote.AlreadyTooled; fingerprint = quote.Fingerprint;
                    return true;
                }
                float dry, fuel;
                launchCost = ship.GetShipCosts(out dry, out fuel, ShipConstruction.ShipManifest);
                alreadyTooled = true;
                return true;
            }
            catch (Exception e) { reason = "quote_error:" + e.GetType().Name; return false; }
        }

        /// <summary>Whether the craft in the editor may be launched by this agency under the trade and research rules (AgencyTradeResearch.ValidateLive).</summary>
        public static bool EditorLaunchAllowance(out string reason)
        {
            reason = null;
            try
            {
                var ship = HighLogic.LoadedSceneIsEditor ? EditorLogic.fetch?.ship : null;
                if (ship == null) { reason = "editor_unavailable"; return false; }
                return AgencyTradeResearch.ValidateLive(ship, out reason);
            }
            catch (Exception e) { reason = "allowance_error:" + e.GetType().Name; return false; }
        }

        /// <summary>
        /// Starts a launch exactly as the editor Launch button does: select the launch site, then run EditorLogic's own launch routine,
        /// whose stock pre-flight checks, crew manifest and FlightDriver.StartWithNewLaunch are intercepted by the tooling reservation.
        /// Nothing here saves a craft, reserves funds or touches a balance. True means the routine was invoked; the outcome is observed
        /// through LaunchPending, the scene and the active vessel.
        /// </summary>
        public static bool BeginLaunch(string launchSiteName, out string reason)
        {
            reason = null;
            try
            {
                var editor = HighLogic.LoadedSceneIsEditor ? EditorLogic.fetch : null;
                if (editor == null || editor.ship == null || editor.ship.Parts.Count == 0) { reason = "editor_unavailable"; return false; }
                if (ToolingClient.LaunchPending) { reason = "launch_pending"; return false; }
                if (string.IsNullOrEmpty(launchSiteName) || !EditorDriver.ValidLaunchSite(launchSiteName)) { reason = "launch_site_invalid"; return false; }
                var select = AccessTools.Method(typeof(EditorDriver), "setLaunchSite", new[] { typeof(string) });
                if (select == null) { reason = "launch_site_selector_unavailable"; return false; }
                select.Invoke(select.IsStatic ? null : EditorDriver.fetch, new object[] { launchSiteName });
                var field = AccessTools.Field(typeof(EditorLogic), "launchSiteName");
                if (field != null && field.FieldType == typeof(string)) field.SetValue(editor, launchSiteName);
                if (!string.Equals(EditorDriver.SelectedLaunchSiteName, launchSiteName, StringComparison.Ordinal)) { reason = "launch_site_not_selected"; return false; }
                var launch = AccessTools.Method(typeof(EditorLogic), "launchVessel", Type.EmptyTypes);
                if (launch == null) { reason = "launch_hook_unavailable"; return false; }
                launch.Invoke(editor, null);
                return true;
            }
            catch (TargetInvocationException e) { reason = "launch_exception:" + (e.InnerException ?? e).GetType().Name; return false; }
            catch (Exception e) { reason = "launch_error:" + e.GetType().Name; return false; }
        }

        /// <summary>Cancels a reservation whose flight scene has not started loading, through the normal CancelLaunch command. False when there is nothing to cancel.</summary>
        public static bool CancelPendingLaunch() => ToolingClient.CancelPendingLaunchIfIdle();
    }
}
