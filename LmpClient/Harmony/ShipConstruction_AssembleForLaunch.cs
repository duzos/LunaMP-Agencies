using HarmonyLib;
using System;
using LmpClient.Diagnostics;
using LmpClient.Events;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Agency;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// This harmony patch is intended to override the "FindVesselsLandedAt" that sometimes is called to check if there are vessels in a launch site
    /// We just remove the other controlled vessels from that check and set them correctly
    /// </summary>
    [HarmonyPatch(typeof(ShipConstruction))]
    [HarmonyPatch("AssembleForLaunch")]
    [HarmonyPatch(new[]
    {
        typeof(ShipConstruct), typeof(string), typeof(string), typeof(string), typeof(Game),
        typeof(VesselCrewManifest), typeof(bool), typeof(bool), typeof(bool), typeof(bool),
        typeof(Orbit), typeof(bool), typeof(bool)
    })]
    public class ShipConstruction_AssembleForLaunch
    {
        [HarmonyPrefix]
        private static void PrefixAssembleForLaunch(ShipConstruct ship, string landedAt, string displaylandedAt, ref string flagURL, Game sceneState, VesselCrewManifest crewManifest,
            bool fromShipAssembly, bool setActiveVessel, bool isLanded, bool preCreate, Orbit orbit, bool orbiting, bool isSplashed)
        {
            if (fromShipAssembly && ship != null)
            {
                ApplyAgencyFlag(ref flagURL);
                VesselAssemblyEvent.onAssemblingVessel.Fire(ship);
            }
        }

        private static bool appliedLogged, notInstalledLogged;

        /// <summary>Re-arms the once-per-session craft flag diagnostics; called when a new server session starts.</summary>
        internal static void ResetDiagnostics()
        {
            appliedLogged = false;
            notInstalledLogged = false;
        }

        /// <summary>
        /// Launch default-flag craft with the agency flag. Only the part flagURL changes: ship.missionFlag is left alone,
        /// and the tooling/trade fingerprints come from the craft file, so they are unaffected.
        /// </summary>
        private static void ApplyAgencyFlag(ref string flagURL)
        {
            try
            {
                var settings = SettingsSystem.CurrentSettings;
                if (settings == null || !settings.AgencyAutoCraftFlag) return;
                var agencyId = AgencySystem.Singleton.MyAgencyId;
                if (agencyId == Guid.Empty || !AgencyIdentityClient.Supported) return;
                var agencyFlag = AgencyIdentityClient.Get(agencyId).FlagUrl;
                var database = GameDatabase.Instance;
                var installed = database != null && !string.IsNullOrEmpty(agencyFlag) && database.ExistsTexture(agencyFlag);
                var gameFlag = HighLogic.CurrentGame?.flagURL;
                if (!AgencyCraftFlagPolicy.ShouldApply(true, agencyId, agencyFlag, installed, flagURL, settings.SelectedFlag, gameFlag))
                {
                    if (!installed && !notInstalledLogged && !string.IsNullOrEmpty(agencyFlag) &&
                        !string.Equals(agencyFlag, AgencyIdentityDefaults.DefaultFlagUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        notInstalledLogged = true;
                        PlaytestDiagnostics.Write("client.agency.craftflag.notinstalled", () => $"flag={agencyFlag}");
                    }
                    return;
                }
                var original = flagURL;
                flagURL = agencyFlag;
                if (!appliedLogged)
                {
                    appliedLogged = true;
                    PlaytestDiagnostics.Write("client.agency.craftflag", () => $"from={original} to={agencyFlag}");
                }
            }
            catch (Exception e)
            {
                PlaytestDiagnostics.Write("client.agency.craftflag.error", () => e.Message);
            }
        }

        [HarmonyPostfix]
        private static void PostfixAssembleForLaunch(ShipConstruct ship, string landedAt, string displaylandedAt, string flagURL, Game sceneState, VesselCrewManifest crewManifest,
            bool fromShipAssembly, bool setActiveVessel, bool isLanded, bool preCreate, Orbit orbit, bool orbiting, bool isSplashed, Vessel __result)
        {
            if (fromShipAssembly && __result && ship != null)
            {
                LmpClient.Systems.Agency.ToolingClient.BindLaunch(__result, ship);
                LmpClient.Systems.Agency.TradeClient.BindLaunch(__result, ship);
                VesselAssemblyEvent.onAssembledVessel.Fire(__result, ship);
            }
        }
    }
}
