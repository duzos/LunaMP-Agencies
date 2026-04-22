using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using System.Collections.Concurrent;
using System.Linq;

namespace LmpClient.Systems.Agency
{
    public class AgencyMessageHandler : SubSystem<AgencySystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is AgencyBaseMsgData data)) return;

            switch (data.AgencyMessageType)
            {
                case AgencyMessageType.SrvSyncAll:
                    Handle((AgencySyncAllMsgData)data);
                    break;
                case AgencyMessageType.SrvUpsert:
                    Handle((AgencyUpsertMsgData)data);
                    break;
                case AgencyMessageType.SrvDelete:
                    Handle((AgencyDeleteMsgData)data);
                    break;
                case AgencyMessageType.SrvJoinRequestPosted:
                    Handle((AgencyJoinRequestPostedMsgData)data);
                    break;
                case AgencyMessageType.SrvJoinRequestResolved:
                    Handle((AgencyJoinRequestResolvedMsgData)data);
                    break;
                case AgencyMessageType.SrvReply:
                    Handle((AgencyReplyMsgData)data);
                    break;
                default:
                    LunaLog.LogWarning($"[Agency] Unhandled Srv subtype {data.AgencyMessageType}");
                    break;
            }
        }

        private static void Handle(AgencySyncAllMsgData data)
        {
            System.KnownAgencies.Clear();
            foreach (var a in data.Agencies)
            {
                if (a != null) System.KnownAgencies[a.Id] = a;
            }
            System.MyAgencyId = data.MyAgencyId;
            LunaLog.Log($"[Agency] SyncAll received: {data.Agencies.Length} agencies; mine={data.MyAgencyId}");
        }

        private static void Handle(AgencyUpsertMsgData data)
        {
            if (data.Agency == null) return;
            System.KnownAgencies[data.Agency.Id] = data.Agency;

            // If the local player is now a member of this agency and was
            // previously in another, update MyAgencyId so the UI reflects it.
            if (data.Agency.MemberUniqueIds != null &&
                data.Agency.MemberUniqueIds.Any(id => id == MainSystem.UniqueIdentifier))
            {
                System.MyAgencyId = data.Agency.Id;
            }
            LunaLog.Log($"[Agency] Upsert '{data.Agency.Name}' id={data.Agency.Id} members={data.Agency.MemberUniqueIds?.Length ?? 0}");
        }

        private static void Handle(AgencyDeleteMsgData data)
        {
            System.KnownAgencies.TryRemove(data.AgencyId, out _);
            if (System.MyAgencyId == data.AgencyId) System.MyAgencyId = global::System.Guid.Empty;
            LunaLog.Log($"[Agency] Delete id={data.AgencyId}");
        }

        private static void Handle(AgencyJoinRequestPostedMsgData data)
        {
            lock (System.RequestsLock)
            {
                if (!System.PendingIncomingRequests.Any(r => r.AgencyId == data.Request.AgencyId && r.PlayerUniqueId == data.Request.PlayerUniqueId))
                    System.PendingIncomingRequests.Add(data.Request);
            }
            LunaLog.Log($"[Agency] JoinRequestPosted by={data.Request.PlayerDisplayName}({data.Request.PlayerUniqueId}) for agency={data.Request.AgencyId}");
            System.PendingServerMessages.Enqueue($"Join request from {data.Request.PlayerDisplayName}");
        }

        private static void Handle(AgencyJoinRequestResolvedMsgData data)
        {
            lock (System.RequestsLock)
            {
                System.PendingIncomingRequests.RemoveAll(r => r.AgencyId == data.AgencyId && r.PlayerUniqueId == data.PlayerUniqueId);
            }
            LunaLog.Log($"[Agency] JoinRequestResolved agency={data.AgencyId} player={data.PlayerUniqueId} approved={data.Approved}");

            if (data.PlayerUniqueId == MainSystem.UniqueIdentifier)
            {
                System.PendingServerMessages.Enqueue(data.Approved
                    ? "Your join request was approved. Reconnect to apply."
                    : "Your join request was rejected.");
                if (data.Approved) System.MyAgencyId = data.AgencyId;
            }
        }

        private static void Handle(AgencyReplyMsgData data)
        {
            if (!string.IsNullOrEmpty(data.Message))
                System.PendingServerMessages.Enqueue((data.Success ? "OK: " : "Err: ") + data.Message);
            LunaLog.Log($"[Agency] Reply success={data.Success} msg={data.Message}");
        }
    }
}
