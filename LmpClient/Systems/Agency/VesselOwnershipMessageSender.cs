using System;
using LmpClient.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Data.Agency;

namespace LmpClient.Systems.Agency
{
    public partial class AgencyMessageSender
    {
        public Guid SendOwnershipCommand(VesselOwnershipOperation operation, Guid vesselId, Guid targetAgencyId = default(Guid), VesselDockingPolicy policy = VesselDockingPolicy.Nobody)
        {
            var data = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyVesselOwnershipCommandMsgData>();
            data.RequestId = Guid.NewGuid(); data.VesselId = vesselId; data.TargetAgencyId = targetAgencyId; data.Operation = operation; data.DockingPolicy = policy;
            var id = data.RequestId; SendMessage(data); return id;
        }
        public Guid RequestDock(Guid sourceVesselId, Guid targetVesselId)
        {
            var data = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyDockRequestMsgData>();
            data.RequestId = Guid.NewGuid(); data.SourceVesselId = sourceVesselId; data.TargetVesselId = targetVesselId;
            var id = data.RequestId; SendMessage(data); return id;
        }
        public void RespondDock(Guid requestId, bool accept)
        {
            var data = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyDockResponseMsgData>();
            data.RequestId = requestId; data.Accept = accept; SendMessage(data);
        }
    }
}
