using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Network;
using LmpClient.Systems.TimeSync;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using System;

namespace LmpClient.Systems.VesselRemoveSys
{
    public class VesselRemoveMessageSender : SubSystem<VesselRemoveSystem>, IMessageSender
    {
        public void SendMessage(IMessageData msg)
        {
            var epoch = LmpClient.Systems.Agency.VesselPublicationGuard.CaptureEpoch();
            TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<VesselCliMsg>(msg), epoch));
        }

        /// <summary>
        /// Sends a vessel remove to the server. If keepVesselInRemoveList is set to true, the vessel will be removed for good and the server
        /// will skip future updates related to this vessel.
        /// <paramref name="reason"/> is a human-readable description (e.g. "Revert to VAB", "Terminated") that the server
        /// records in its craft create/remove audit log.
        /// </summary>
        public void SendVesselRemove(Vessel vessel, bool keepVesselInRemoveList = true, string reason = null)
        {
            if (vessel == null) return;

            SendVesselRemove(vessel.id, keepVesselInRemoveList, reason);
        }

        /// <summary>
        /// Sends a vessel remove to the server. If keepVesselInRemoveList is set to true, the vessel will be removed for good and the server
        /// will skip future updates related to this vessel.
        /// <paramref name="reason"/> is a human-readable description (e.g. "Revert to VAB", "Terminated") that the server
        /// records in its craft create/remove audit log.
        /// </summary>
        public void SendVesselRemove(Guid vesselId, bool keepVesselInRemoveList = true, string reason = null)
        {
            if (!LmpClient.Systems.Agency.VesselPublicationGuard.RetainRemoval(vesselId, keepVesselInRemoveList, reason,
                TimeSyncSystem.UniversalTime, out var overflow))
            {
                if (overflow)
                {
                    LmpClient.Systems.Agency.ToolingClient.RecoveryDisconnect("Too many pending vessel removals.");
                    return;
                }
                Diagnostics.PlaytestDiagnostics.Write("client.vessel.remove-blocked", () => $"vessel={vesselId} permanent={keepVesselInRemoveList} reason={reason}");
                return;
            }
            LunaLog.Log($"[LMP]: Removing {vesselId} from the server ({reason ?? "Unknown reason"})");
            Diagnostics.PlaytestDiagnostics.Write("client.vessel.remove-retained", () => $"vessel={vesselId} permanent={keepVesselInRemoveList} blocked={LmpClient.Systems.Agency.VesselPublicationGuard.Pending} reason={reason}");
            FlushRetainedRemovals();
        }

        public static void FlushRetainedRemovals() => LmpClient.Systems.Agency.VesselPublicationGuard.PumpRemovals(
            () => NetworkMain.CliMsgFactory.CreateNew<VesselCliMsg>(NetworkMain.CliMsgFactory.CreateNewMessageData<VesselRemoveMsgData>()),
            (message, epoch) => NetworkSender.QueueOutgoingMessage(message, epoch));
    }
}
