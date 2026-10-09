using System;
using HarmonyLib;
using LmpClient.Diagnostics;
using LmpClient.Windows.Agency;

namespace LmpClient.Harmony
{
    /// <summary>
    /// Plan 41 host for agency presentation patches. Final form, owned by S0: S1 (AgencyMapPresentation),
    /// S3 (AgencyNameplateOverlay) and S4 (AgencySiteCaption, LaunchSiteFlagOverlay) fill in the members
    /// called here with these exact signatures and never edit this file.
    /// </summary>
    internal static class AgencyPresentationPatches
    {
        internal static void Install(HarmonyLib.Harmony harmony)
        {
            try { AgencyMapPresentation.Install(harmony); }
            catch (Exception e) { Fail("map", e); }
            try { AgencySiteCaption.Install(harmony); }
            catch (Exception e) { Fail("site-caption", e); }
            try
            {
                var onGui = AccessTools.Method(typeof(MainSystem), "OnGUI", Type.EmptyTypes) ?? throw new MissingMethodException(typeof(MainSystem).FullName, "OnGUI");
                harmony.Patch(onGui, postfix: new HarmonyMethod(typeof(AgencyPresentationPatches), nameof(DrawOverlays)));
            }
            catch (Exception e) { Fail("overlays", e); }
        }

        private static void Fail(string part, Exception e)
        {
            LunaLog.LogError("[AgencyPresentation] Could not install " + part + " presentation: " + e);
            PlaytestDiagnostics.Write("client.agency-presentation.install-error", () => "part=" + part + " error=" + e.Message);
        }

        private static void DrawOverlays()
        {
            try { AgencyNameplateOverlay.Draw(); }
            catch (Exception e) { PlaytestDiagnostics.Write("client.agency-presentation.nameplate-error", () => e.Message); }
            try { LaunchSiteFlagOverlay.Draw(); }
            catch (Exception e) { PlaytestDiagnostics.Write("client.agency-presentation.site-flag-error", () => e.Message); }
        }
    }
}
