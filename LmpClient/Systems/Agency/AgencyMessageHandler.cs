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

            LmpClient.Diagnostics.PlaytestDiagnostics.Write("client.agency.receive", () => $"subtype={data.AgencyMessageType} agency={System.MyAgencyId}", traffic: true);
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
                case AgencyMessageType.SrvVesselMapSync:
                    Handle((AgencyVesselMapSyncMsgData)data);
                    break;
                case AgencyMessageType.SrvVesselMapEntry:
                    Handle((AgencyVesselMapEntryMsgData)data);
                    break;
                case AgencyMessageType.SrvDockStatus:
                    var dock = (AgencyDockStatusMsgData)data;
                    System.ApplyDockStatus(new DockConsentSnapshot { RequestId = dock.RequestId, SourceVesselId = dock.SourceVesselId, TargetVesselId = dock.TargetVesselId,
                        RequesterAgencyId = dock.RequesterAgencyId, RequesterName = dock.RequesterName, Status = dock.Status, ExpiresUtcTicks = dock.ExpiresUtcTicks,
                        Reason = dock.Reason, GrantId = dock.GrantId, OperationId = dock.OperationId });
                    break;
                case AgencyMessageType.SrvVesselOwnershipResult:
                    var result = (AgencyVesselOwnershipResultMsgData)data;
                    System.ApplyOwnershipResult(new OwnershipResultSnapshot { RequestId = result.RequestId, VesselId = result.VesselId, Success = result.Success, Reason = result.Reason });
                    break;
                case AgencyMessageType.SrvCommNetSnapshot:
                    System.ApplyCommNet((AgencyCommNetSnapshotMsgData)data);
                    break;
                case AgencyMessageType.SrvCommNetResult:
                    System.ApplyCommNetResult((AgencyCommNetResultMsgData)data);
                    break;
                case AgencyMessageType.SrvEconomySnapshot:
                    ToolingClient.Receive(((AgencyEconomySnapshotMsgData)data).Snapshot);
                    break;
                case AgencyMessageType.SrvEconomyResult:
                    ToolingClient.Receive(((AgencyEconomyResultMsgData)data).Result);
                    break;
                default:
                    LunaLog.LogWarning($"[Agency] Unhandled Srv subtype {data.AgencyMessageType}");
                    break;
            }
            LmpClient.Diagnostics.PlaytestDiagnostics.RequestSnapshot();
        }

        private static void Handle(AgencyVesselMapSyncMsgData data)
        {
            System.InvalidateCommNet();
            System.ApplyOwnership(data.OwnershipSnapshotPresent, data.OwnershipRevision, data.OwnershipRecords, true);
            System.VesselAgencyMap.Clear();
            for (int i = 0; i < data.VesselIds.Length; i++)
                System.VesselAgencyMap[data.VesselIds[i]] = data.AgencyIds[i];
            LunaLog.Log($"[Agency] VesselMapSync received: {data.VesselIds.Length} vessels.");
        }

        private static void Handle(AgencyVesselMapEntryMsgData data)
        {
            System.InvalidateCommNet();
            System.ApplyOwnershipEntry(data);
            if (data.AgencyId == global::System.Guid.Empty)
                System.VesselAgencyMap.TryRemove(data.VesselId, out _);
            else
                System.VesselAgencyMap[data.VesselId] = data.AgencyId;
        }

        private static void Handle(AgencySyncAllMsgData data)
        {
            System.KnownAgencies.Clear();
            foreach (var a in data.Agencies)
            {
                if (a != null) System.KnownAgencies[a.Id] = a;
            }
            System.MyAgencyId = data.MyAgencyId;
            System.ApplyLaunchSites(data);
            LmpClient.Diagnostics.PlaytestDiagnostics.Write("client.agency.sync-applied", () => $"agency={data.MyAgencyId} count={data.Agencies.Length}");
            LunaLog.Log($"[Agency] SyncAll received: {data.Agencies.Length} agencies; mine={data.MyAgencyId}");
        }

        private static void Handle(AgencyUpsertMsgData data)
        {
            if (data.Agency == null) return;
            System.KnownAgencies[data.Agency.Id] = data.Agency;
            LmpClient.Diagnostics.PlaytestDiagnostics.Write("client.agency.upsert-applied", () => $"agency={data.Agency.Id} members={data.Agency.MemberUniqueIds?.Length ?? 0} funds={data.Agency.Funds} science={data.Agency.Science} reputation={data.Agency.Reputation}");

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
            LmpClient.Diagnostics.PlaytestDiagnostics.Write("client.agency.join-resolved", () => $"agency={data.AgencyId} approved={data.Approved} localPlayer={data.PlayerUniqueId == MainSystem.UniqueIdentifier}");
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
            System.LatestServerReply = (data.Success ? "OK: " : "Err: ") + data.Message;
            LmpClient.Diagnostics.PlaytestDiagnostics.Write("client.agency.reply", () => $"success={data.Success} messagePresent={!string.IsNullOrEmpty(data.Message)} agency={System.MyAgencyId}");
            if (!string.IsNullOrEmpty(data.Message))
                System.PendingServerMessages.Enqueue((data.Success ? "OK: " : "Err: ") + data.Message);
            LunaLog.Log($"[Agency] Reply success={data.Success} msg={data.Message}");
        }
    }
}
