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
                // Player launches only: contract and mission spawns (ContractSystem, MissionSystem) also come through here
                // with fromShipAssembly but never set the active vessel.
                if (setActiveVessel) ApplyAgencyFlag(ref flagURL, ship.missionFlag);
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
        /// Launch craft that are still on the stock default flag with the agency flag. Stock AssembleForLaunch copies
        /// this one flagURL onto every part's Part.flagURL (what FlagDecal shows), so swapping it here is the same as
        /// choosing the mission flag. Flag parts keep their own FlagDecalBackground.currentflagUrl and are never touched.
        /// ship.missionFlag is left alone, and the tooling/trade fingerprints come from the craft file.
        /// </summary>
        private static void ApplyAgencyFlag(ref string flagURL, string craftMissionFlag)
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
                if (!AgencyCraftFlagPolicy.ShouldApply(true, agencyId, agencyFlag, installed, flagURL, craftMissionFlag))
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
