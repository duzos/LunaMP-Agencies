namespace LmpCommon.Message.Types
{
    /// <summary>
    /// Subtypes carried by AgencyCliMsg / AgencySrvMsg.
    /// Server-bound values (Cli*) are sent from client to server.
    /// Client-bound values (Srv*) are sent from server to clients.
    /// </summary>
    public enum AgencyMessageType
    {
        // Server -> Client
        SrvSyncAll = 0,
        SrvUpsert = 1,
        SrvDelete = 2,
        SrvJoinRequestPosted = 3,
        SrvJoinRequestResolved = 4,
        SrvReply = 5,
        SrvVesselMapSync = 6,
        SrvVesselMapEntry = 7,
        SrvDockStatus = 8,
        SrvVesselOwnershipResult = 9,
        SrvCommNetSnapshot = 10,
        SrvCommNetResult = 11,
        CliCommNetCommand = 33,
        CliVesselOwnershipCommand = 30,
        CliDockRequest = 31,
        CliDockResponse = 32,

        // Client -> Server (player actions)
        CliCreate = 20,
        CliRename = 21,
        CliJoinRequest = 22,
        CliLeave = 23,
        CliApproveJoin = 24,
        CliRejectJoin = 25,
        CliKickMember = 26,
        CliTransferOwner = 27,
        CliCancelJoinRequest = 28,
        CliTransferResources = 29,

        // Client -> Server (admin actions)
        CliAdminOp = 50,
    }
}
