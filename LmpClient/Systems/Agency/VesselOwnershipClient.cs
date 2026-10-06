using System;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselLockSys;
using LmpClient.VesselUtilities;
using LmpCommon.Agency;
using LmpCommon.Locks;

namespace LmpClient.Systems.Agency
{
    public static class VesselOwnershipClient
    {
        private static DateTime nextReconcile;
        [ThreadStatic] private static int splitDepth;
        internal static void BeginSplit() { splitDepth++; }
        internal static void EndSplit()
        {
            if (splitDepth > 0) splitDepth--;
            if (splitDepth == 0 && HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel)
                HandleVesselChange(FlightGlobals.ActiveVessel);
        }
        public static bool InLocalSplit => splitDepth > 0;
        public static bool HasConfirmedControl(Guid vessel)
        {
            return !AgencySystem.OwnershipEnabled || (AgencySystem.Singleton.CanControlVessel(vessel) &&
                LockSystem.LockQuery.ControlLockBelongsToPlayer(vessel, SettingsSystem.CurrentSettings.PlayerName));
        }
        public static bool HandleVesselChange(Vessel vessel)
        {
            if (!AgencySystem.OwnershipEnabled) return false;
            if (VesselPublicationGuard.Pending || InLocalSplit) return true;
            if (!vessel || HasConfirmedControl(vessel.id)) return false;
            if (!VesselCommon.IsSpectating) VesselLockSystem.Singleton.StartSpectating(vessel.id);
            if (AgencySystem.Singleton.CanControlVessel(vessel.id)) LockSystem.Singleton.AcquireControlLock(vessel.id);
            return true;
        }
        internal static void Tick()
        {
            var agency = AgencySystem.Singleton;
            if (agency.ResetOwnershipUi)
            {
                agency.ResetOwnershipUi = false;
                DockingCoordinator.Clear();
                Windows.Agency.AgencyWindow.ResetVesselOwnershipUi();
            }
            while (agency.DockNotifications.TryDequeue(out var status))
            {
                if (!DockingCoordinator.Enabled) continue;
                DockingCoordinator.HandleStatus(status);
                if (status.Status == DockConsentStatus.Pending && status.ExpiresUtcTicks > DateTime.UtcNow.Ticks)
                {
                    var snapshot = agency.GetOwnershipSnapshot();
                    if (snapshot.TryGetValue(status.TargetVesselId, out var record) && record.OwnerAgencyId == agency.MyAgencyId)
                        Windows.Agency.AgencyWindow.NotifyDockRequest();
                }
            }
            DockingCoordinator.Tick();
            if (!AgencySystem.OwnershipEnabled || VesselPublicationGuard.Pending || DateTime.UtcNow < nextReconcile || !HighLogic.LoadedSceneIsFlight || !FlightGlobals.ActiveVessel) return;
            nextReconcile = DateTime.UtcNow.AddMilliseconds(500);
            var vessel = FlightGlobals.ActiveVessel;
            if (!HasConfirmedControl(vessel.id)) HandleVesselChange(vessel);
            else if (VesselCommon.IsSpectating) VesselLockSystem.Singleton.StopSpectating();
        }
    }
}
