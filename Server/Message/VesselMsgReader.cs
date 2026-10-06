using Server.Agency;
using LmpCommon.Agency;
using Server.Diagnostics;
using ByteSizeLib;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using LmpCommon.Message.Types;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Message.Base;
using Server.Server;
using Server.System;
using Server.System.Vessel;
using System;
using System.Linq;
using System.Text;

namespace Server.Message
{
    public class VesselMsgReader : ReaderBase
    {
        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            var messageData = message.Data as VesselBaseMsgData;
            if (VesselOwnershipSystem.Enabled && (VesselOwnershipSystem.IsRejected(client) || !AgencyVesselMap.Ready || (messageData != null && AgencyVesselMap.IsAbsorbed(messageData.VesselId) && messageData.VesselMessageType != VesselMessageType.Couple))) return;
            switch (messageData?.VesselMessageType)
            {
                case VesselMessageType.Sync:
                    HandleVesselsSync(client, messageData);
                    message.Recycle();
                    break;
                case VesselMessageType.Proto:
                    HandleVesselProto(client, messageData);
                    break;
                case VesselMessageType.Remove:
                    HandleVesselRemove(client, messageData);
                    break;
                case VesselMessageType.Position:
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    if (client.Subspace == WarpContext.LatestSubspace.Id)
                        VesselDataUpdater.WritePositionDataToFile(messageData);
                    break;
                case VesselMessageType.Flightstate:
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    VesselDataUpdater.WriteFlightstateDataToFile(messageData);
                    break;
                case VesselMessageType.Update:
                    VesselDataUpdater.WriteUpdateDataToFile(messageData);
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Resource:
                    VesselDataUpdater.WriteResourceDataToFile(messageData);
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.PartSyncField:
                    VesselDataUpdater.WritePartSyncFieldDataToFile(messageData);
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.PartSyncUiField:
                    VesselDataUpdater.WritePartSyncUiFieldDataToFile(messageData);
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.PartSyncCall:
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.ActionGroup:
                    VesselDataUpdater.WriteActionGroupDataToFile(messageData);
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Fairing:
                    VesselDataUpdater.WriteFairingDataToFile(messageData);
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Decouple:
                    if (VesselOwnershipSystem.Enabled) { var split=(VesselDecoupleMsgData)messageData; lock(AgencyVesselMap.TransactionGate) if(!VesselOwnershipSystem.CanControl(client,split.VesselId) || !AgencyVesselMap.RestoreSplit(split.VesselId,split.NewVesselId,0,split.PartFlightId)) return; VesselOwnershipSystem.Changed(); }
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Couple:
                    HandleVesselCouple(client, messageData);
                    break;
                case VesselMessageType.Undock:
                    if (VesselOwnershipSystem.Enabled) { var split=(VesselUndockMsgData)messageData; lock(AgencyVesselMap.TransactionGate) if(!VesselOwnershipSystem.CanControl(client,split.VesselId) || !AgencyVesselMap.RestoreSplit(split.VesselId,split.NewVesselId,split.DockedInfoRootPartUId,split.PartFlightId)) return; VesselOwnershipSystem.Changed(); }
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                default:
                    throw new NotImplementedException("Vessel message type not implemented");
            }
        }

        private static void HandleVesselRemove(ClientStructure client, VesselBaseMsgData message)
        {
            var data = (VesselRemoveMsgData)message;
            if(VesselOwnershipSystem.Enabled)
            {
                string removedName;
                bool existed;
                lock(AgencyVesselMap.TransactionGate)
                {
                    if(!VesselOwnershipSystem.CanControl(client,data.VesselId) || (LockSystem.LockQuery.ControlLockExists(data.VesselId) && !LockSystem.LockQuery.ControlLockBelongsToPlayer(data.VesselId,client.PlayerName))) return;
                    existed = VesselStoreSystem.VesselExists(data.VesselId);
                    removedName = existed ? TryGetVesselName(data.VesselId) : null;
                    AgencyVesselMap.Remove(data.VesselId);
                    if(data.AddToKillList) VesselContext.RemovedVessels.TryAdd(data.VesselId,0);
                    VesselStoreSystem.RemoveVessel(data.VesselId);
                }
                if (existed) CraftCreationAndRemovalLog.LogRemoved(data.VesselId, removedName, client.PlayerName, data.Reason);
                VesselOwnershipSystem.Changed();MessageQueuer.RelayMessage<VesselSrvMsg>(client,data);return;
            }

            if (LockSystem.LockQuery.ControlLockExists(data.VesselId) && !LockSystem.LockQuery.ControlLockBelongsToPlayer(data.VesselId, client.PlayerName))
                return;


            // Publish the kill-list entry BEFORE touching the store so any in-flight proto task
            // (VesselDataUpdater.RawConfigNodeInsertOrUpdate schedules its store write on Task.Run)
            // observes the flag and aborts instead of resurrecting the vessel we're about to remove.
            if (data.AddToKillList)
                VesselContext.RemovedVessels.TryAdd(data.VesselId, 0);

            if (VesselStoreSystem.VesselExists(data.VesselId))
            {
                // Resolve the vessel name BEFORE RemoveVessel purges the store entry so the audit log line has something useful.
                var vesselName = TryGetVesselName(data.VesselId);

                LunaLog.Debug($"Removing vessel {data.VesselId} from {client.PlayerName}");
                CraftCreationAndRemovalLog.LogRemoved(data.VesselId, vesselName, client.PlayerName, data.Reason);

                VesselStoreSystem.RemoveVessel(data.VesselId);
            }

            //Relay the message.
            MessageQueuer.RelayMessage<VesselSrvMsg>(client, data);
        }

        private static void HandleVesselProto(ClientStructure client, VesselBaseMsgData message)
        {
            var msgData = (VesselProtoMsgData)message;

            if (VesselContext.RemovedVessels.ContainsKey(msgData.VesselId))
            {
                PlaytestDiagnostics.Write("vessel.proto.reject", () => $"{PlaytestDiagnostics.Client(client)} vessel={msgData.VesselId} reason=removed");
                return;
            }

            if (msgData.NumBytes == 0)
            {
                PlaytestDiagnostics.Write("vessel.proto.reject", () => $"{PlaytestDiagnostics.Client(client)} vessel={msgData.VesselId} reason=empty");
                LunaLog.Warning($"Received a vessel with 0 bytes ({msgData.VesselId}) from {client.PlayerName}.");
                return;
            }

            var vesselText = Encoding.UTF8.GetString(msgData.Data, 0, msgData.NumBytes);
            if(VesselOwnershipSystem.Enabled)
            {
                lock(AgencyVesselMap.TransactionGate)
                {
                    if(!AgencyVesselMap.Ready || AgencyVesselMap.IsAbsorbed(msgData.VesselId) || VesselContext.RemovedVessels.ContainsKey(msgData.VesselId)) return;
                    global::Server.System.Vessel.Classes.Vessel parsed;
                    try { parsed=new global::Server.System.Vessel.Classes.Vessel(vesselText); } catch { return; }
                    if(!Guid.TryParse(parsed.Fields.GetSingle("pid")?.Value,out var parsedId) || parsedId!=msgData.VesselId) return;
                    if(global::Server.Settings.Structures.GeneralSettings.SettingsStore.ModControl && parsed.Parts.GetAllValues().Select(p=>p.Fields.GetSingle("name").Value).Except(ModFileSystem.ModControl.AllowedParts).Any()) return;
                    var topologyChild=AgencyVesselMap.IsPendingSplit(msgData.VesselId) || AgencyVesselMap.Get(msgData.VesselId)!=null;
                    if(!AgencyVesselMap.ResolveSplit(msgData.VesselId,AgencyVesselMap.PartIds(parsed))) return;
                    var existing=VesselStoreSystem.VesselExists(msgData.VesselId);
                    if(!existing) AgencyVesselMap.RegisterNew(msgData.VesselId,client.AgencyId);
                    VesselStoreSystem.CurrentVessels[msgData.VesselId]=parsed;
                    if(!existing && !topologyChild)
                    {
                        var agency=AgencySystem.GetAgency(client.AgencyId);
                        if(agency!=null) { bool counted;lock(agency.Lock) { counted=agency.CountedVesselIds.Add(msgData.VesselId);if(counted) agency.VesselsLaunched++; } if(counted) { AgencyStore.PersistAgency(agency);AgencyNetwork.BroadcastUpsert(agency); } }
                        CraftCreationAndRemovalLog.LogCreated(msgData.VesselId,CraftCreationAndRemovalLog.ExtractVesselName(vesselText),client.PlayerName,msgData.Reason);
                    }
                }
                AgencyNetwork.BroadcastVesselMapEntry(msgData.VesselId,AgencyVesselMap.Get(msgData.VesselId)?.OwnerAgencyId??Guid.Empty);
                MessageQueuer.RelayMessage<VesselSrvMsg>(client,msgData);
                return;
            }
            var isNewVessel = !VesselStoreSystem.VesselExists(msgData.VesselId);
            PlaytestDiagnostics.Write("vessel.proto", () => $"{PlaytestDiagnostics.Client(client)} vessel={msgData.VesselId} firstSeen={isNewVessel} bytes={msgData.NumBytes}", true);
            if (isNewVessel)
            {
                LunaLog.Debug($"Saving vessel {msgData.VesselId} ({ByteSize.FromBytes(msgData.NumBytes).KiloBytes} KB) from {client.PlayerName}.");

                // Leaderboard: count this vessel toward the launching agency,
                // but only the first time we see it. CountedVesselIds keeps
                // the increment idempotent across re-syncs and crashes.
                var agency = global::Server.Agency.AgencySystem.GetAgency(client.AgencyId);
                if (agency != null)
                {
                    bool counted;
                    lock (agency.Lock)
                    {
                        counted = agency.CountedVesselIds.Add(msgData.VesselId);
                        if (counted) agency.VesselsLaunched++;
                    }
                    PlaytestDiagnostics.Write("vessel.ownership", () => $"agency={agency.Id} vessel={msgData.VesselId} counted={counted} result=assigned");
                    if (counted)
                    {
                        global::Server.Agency.AgencyStore.PersistAgency(agency);
                        global::Server.Agency.AgencyNetwork.BroadcastUpsert(agency);
                    }

                    // Record vessel→agency for the per-agency CommNet filter.
                    global::Server.Agency.AgencyVesselMap.Set(msgData.VesselId, agency.Id);
                    global::Server.Agency.AgencyNetwork.BroadcastVesselMapEntry(msgData.VesselId, agency.Id);
                }
                // Audit-log first-time vessel registrations. Use the raw config-node text to pull
                // the name out cheaply without allocating another Vessel instance here - the
                // authoritative parse still happens inside VesselDataUpdater below.
                var vesselName = CraftCreationAndRemovalLog.ExtractVesselName(vesselText);
                CraftCreationAndRemovalLog.LogCreated(msgData.VesselId, vesselName, client.PlayerName, msgData.Reason);
            }

            VesselDataUpdater.RawConfigNodeInsertOrUpdate(msgData.VesselId, vesselText);
            MessageQueuer.RelayMessage<VesselSrvMsg>(client, msgData);
        }

        /// <summary>
        /// Looks up a vessel's display name from the in-memory store. Returns <c>null</c> if the
        /// vessel is not present or the name field is missing/malformed.
        /// </summary>
        private static string TryGetVesselName(Guid vesselId)
        {
            if (!VesselStoreSystem.CurrentVessels.TryGetValue(vesselId, out var vessel))
                return null;

            try
            {
                return vessel.Fields.GetSingle("name")?.Value;
            }
            catch
            {
                return null;
            }
        }

        private static void HandleVesselsSync(ClientStructure client, VesselBaseMsgData message)
        {
            var msgData = (VesselSyncMsgData)message;

            var allVessels = VesselStoreSystem.CurrentVessels.Keys.ToList();

            //Here we only remove the vessels that the client ALREADY HAS so we only send the vessels they DON'T have
            for (var i = 0; i < msgData.VesselsCount; i++)
                allVessels.Remove(msgData.VesselIds[i]);

            var vesselsToSend = allVessels;
            foreach (var vesselId in vesselsToSend)
            {
                var vesselData = VesselStoreSystem.GetVesselInConfigNodeFormat(vesselId);
                if (vesselData.Length > 0)
                {
                    var protoMsg = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
                    var vesselBytes = Encoding.UTF8.GetBytes(vesselData);
                    protoMsg.Data = vesselBytes;
                    protoMsg.NumBytes = vesselBytes.Length;
                    protoMsg.VesselId = vesselId;

                    MessageQueuer.SendToClient<VesselSrvMsg>(client, protoMsg);
                }
            }

            if (allVessels.Count > 0)
                LunaLog.Debug($"Sending {client.PlayerName} {vesselsToSend.Count} vessels");
        }

        private static void HandleVesselCouple(ClientStructure client, VesselBaseMsgData message)
        {
            var msgData = (VesselCoupleMsgData)message;

            if(VesselOwnershipSystem.Enabled)
            {
                var removedName = TryGetVesselName(msgData.CoupledVesselId);
                if(!VesselOwnershipSystem.Couple(client,msgData,out var newlyApplied) || !newlyApplied) return;
                CraftCreationAndRemovalLog.LogRemoved(msgData.CoupledVesselId, removedName, client.PlayerName, "Coupled/Docked");
                MessageQueuer.RelayMessage<VesselSrvMsg>(client,msgData);
                var merged=ServerContext.ServerMessageFactory.CreateNewMessageData<VesselProtoMsgData>(); merged.VesselId=msgData.VesselId; merged.ForceReload=true;merged.Data=msgData.MergedVesselData;merged.NumBytes=merged.Data.Length;merged.Reason="Authoritative coupled vessel";MessageQueuer.RelayMessage<VesselSrvMsg>(client,merged);
                var removed=ServerContext.ServerMessageFactory.CreateNewMessageData<VesselRemoveMsgData>(); removed.VesselId=msgData.CoupledVesselId;removed.Reason="Coupled/Docked";MessageQueuer.SendToAllClients<VesselSrvMsg>(removed);
                return;
            }
            LunaLog.Debug($"Coupling message received! Dominant vessel: {msgData.VesselId}");
            MessageQueuer.RelayMessage<VesselSrvMsg>(client, msgData);

            if (VesselContext.RemovedVessels.ContainsKey(msgData.CoupledVesselId)) return;

            //Now remove the weak vessel but DO NOT add to the removed vessels as they might undock!!!
            LunaLog.Debug($"Removing weak coupled vessel {msgData.CoupledVesselId}");

            // Audit-log the implicit removal triggered by a docking/coupling event. Name must be
            // resolved BEFORE RemoveVessel clears the store entry.
            var coupledVesselName = TryGetVesselName(msgData.CoupledVesselId);
            CraftCreationAndRemovalLog.LogRemoved(msgData.CoupledVesselId, coupledVesselName, client.PlayerName, "Coupled/Docked");

            VesselStoreSystem.RemoveVessel(msgData.CoupledVesselId);

            //Tell all clients to remove the weak vessel
            var removeMsgData = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselRemoveMsgData>();
            removeMsgData.VesselId = msgData.CoupledVesselId;
            removeMsgData.Reason = "Coupled/Docked";

            MessageQueuer.SendToAllClients<VesselSrvMsg>(removeMsgData);
        }
    }
}
