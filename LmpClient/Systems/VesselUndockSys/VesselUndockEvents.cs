using LmpClient.Base;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselPositionSys;
using LmpClient.VesselUtilities;

namespace LmpClient.Systems.VesselUndockSys
{
    public class VesselUndockEvents : SubSystem<VesselUndockSystem>
    {
        public void UndockStart(Part part, DockedVesselInfo dockedInfo)
        {
            if (VesselCommon.IsSpectating || System.IgnoreEvents) return;
            if (!LockSystem.LockQuery.UpdateLockBelongsToPlayer(part.vessel.id, SettingsSystem.CurrentSettings.PlayerName)) return;

            LunaLog.Log($"Detected undock! Part: {part.partName} Vessel: {part.vessel.id}");
        }

        public void UndockComplete(Part part, DockedVesselInfo dockedInfo, Vessel originalVessel)
        {
            if (VesselCommon.IsSpectating || System.IgnoreEvents) return;
            if (!LockSystem.LockQuery.UpdateLockBelongsToPlayer(originalVessel.id, SettingsSystem.CurrentSettings.PlayerName)) return;

            LockSystem.Singleton.AcquireUnloadedUpdateLock(part.vessel.id, true, true);
            LockSystem.Singleton.AcquireUpdateLock(part.vessel.id, true, true);

            if (LmpClient.Systems.Agency.ToolingClient.Enabled)
            {
                LmpClient.Systems.VesselProtoSys.LocalTopologyTracker.RecordMutation(part.vessel.id);
                LmpClient.Systems.VesselProtoSys.LocalTopologyTracker.RecordMutation(originalVessel.id);
                LmpClient.Systems.Agency.ToolingClient.CaptureSplit(originalVessel, part.vessel, part.flightID, 0, dockedInfo);
                return;
            }
            VesselPositionSystem.Singleton.MessageSender.SendVesselPositionUpdate(part.vessel, true);

            LunaLog.Log($"Undock complete! Part: {part} Vessel: {originalVessel.id}");
            System.MessageSender.SendVesselUndock(originalVessel, part.flightID, dockedInfo, part.vessel.id);
            if (LmpClient.Systems.Agency.DockingCoordinator.Enabled)
            {
                LmpClient.Systems.VesselProtoSys.LocalTopologyTracker.RecordMutation(part.vessel.id);
                LmpClient.Systems.VesselProtoSys.LocalTopologyTracker.RecordMutation(originalVessel.id);
                LmpClient.Systems.VesselProtoSys.VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(part.vessel, reason: "Agency split child");
                LmpClient.Systems.VesselProtoSys.VesselProtoSystem.Singleton.DelayedSendVesselMessage(originalVessel.id, 0.5f, reason: "Agency split parent");
            }
        }
    }
}
