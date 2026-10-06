using LmpClient.Base;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselPositionSys;
using LmpClient.VesselUtilities;

namespace LmpClient.Systems.VesselDecoupleSys
{
    public class VesselDecoupleEvents : SubSystem<VesselDecoupleSystem>
    {
        public void DecoupleStart(Part part, float breakForce)
        {
            if (VesselCommon.IsSpectating || System.IgnoreEvents || !part) return;
            if (!LockSystem.LockQuery.UpdateLockBelongsToPlayer(part.vessel.id, SettingsSystem.CurrentSettings.PlayerName)) return;

            LunaLog.Log($"Detected decouple! Part: {part.partName} Vessel: {part.vessel.id}");
        }

        public void DecoupleComplete(Part part, float breakForce, Vessel originalVessel)
        {
            if (VesselCommon.IsSpectating || System.IgnoreEvents || !part || !originalVessel) return;
            if (!LockSystem.LockQuery.UpdateLockBelongsToPlayer(originalVessel.id, SettingsSystem.CurrentSettings.PlayerName)) return;

            LockSystem.Singleton.AcquireUnloadedUpdateLock(part.vessel.id, true, true);
            LockSystem.Singleton.AcquireUpdateLock(part.vessel.id, true, true);

            if (LmpClient.Systems.Agency.ToolingClient.Enabled)
            {
                LmpClient.Systems.VesselProtoSys.LocalTopologyTracker.RecordMutation(part.vessel.id);
                LmpClient.Systems.VesselProtoSys.LocalTopologyTracker.RecordMutation(originalVessel.id);
                LmpClient.Systems.Agency.ToolingClient.CaptureSplit(originalVessel, part.vessel, part.flightID, breakForce);
                return;
            }
            VesselPositionSystem.Singleton.MessageSender.SendVesselPositionUpdate(part.vessel, true);

            LunaLog.Log($"Decouple complete! Part: {part.partName} Vessel: {part.vessel.id}");
            System.MessageSender.SendVesselDecouple(originalVessel, part.flightID, breakForce, part.vessel.id);
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
