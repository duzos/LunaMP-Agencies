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
            if (!AgencyEconomyStore.MayPublish(client)) { RejectSplit(client, messageData, "Publication is blocked."); return; }
            if ((VesselOwnershipSystem.Enabled || AgencyEconomyStore.ToolingEnabled) && (VesselOwnershipSystem.IsRejected(client) || !AgencyVesselMap.Ready || (messageData != null && AgencyVesselMap.IsAbsorbed(messageData.VesselId) && messageData.VesselMessageType != VesselMessageType.Couple))) { RejectSplit(client, messageData, "Split was rejected by the server."); return; }
            switch (messageData?.VesselMessageType)
            {
                case VesselMessageType.Sync:
                    HandleVesselsSync(client, messageData);
                    message.Recycle();
                    break;
                case VesselMessageType.Proto:
                    HandleVesselProtoAnswered(client, messageData);
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
                // A refused Decouple/Undock below returns silently: it carries no operation id. The child proto that follows on the same
                // reliable-ordered channel finds no pending split and is answered with a failed Split result (see AnswerSplit).
                case VesselMessageType.Decouple:
                    if (VesselOwnershipSystem.Enabled || AgencyEconomyStore.ToolingEnabled) { var split=(VesselDecoupleMsgData)messageData; lock(AgencyVesselMap.TransactionGate) if(!VesselOwnershipSystem.CanControl(client,split.VesselId) || !AgencyVesselMap.RestoreSplit(split.VesselId,split.NewVesselId,0,split.PartFlightId,null,client.UniqueIdentifier,client.ConnectionTime.Ticks)) return; VesselOwnershipSystem.Changed(); }
                    if (!AgencyEconomyStore.ToolingEnabled) MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Couple:
                    HandleVesselCouple(client, messageData);
                    break;
                case VesselMessageType.Undock:
                    if (VesselOwnershipSystem.Enabled || AgencyEconomyStore.ToolingEnabled) { var split=(VesselUndockMsgData)messageData; lock(AgencyVesselMap.TransactionGate) if(!VesselOwnershipSystem.CanControl(client,split.VesselId) || !AgencyVesselMap.RestoreSplit(split.VesselId,split.NewVesselId,split.DockedInfoRootPartUId,split.PartFlightId,null,client.UniqueIdentifier,client.ConnectionTime.Ticks)) return; VesselOwnershipSystem.Changed(); }
                    if (!AgencyEconomyStore.ToolingEnabled) MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                default:
                    throw new NotImplementedException("Vessel message type not implemented");
            }
        }

        private static void HandleVesselRemove(ClientStructure client, VesselBaseMsgData message)
        {
            var data = (VesselRemoveMsgData)message;
            // Authorization is checked per id under the removal gate; every mode removes through the central service.
            VesselRemovalService.Remove(new[] { data.VesselId }, data.Reason, VesselRemovalMode.Ordinary, client, new VesselRemovalOptions
            {
                Permanent = data.AddToKillList, ClientKillList = data.AddToKillList, Original = data, Target = VesselRemoveTarget.OtherClients,
                Authorize = id => VesselOwnershipSystem.CanControl(client, id) && !(LockSystem.LockQuery.ControlLockExists(id) && !LockSystem.LockQuery.ControlLockBelongsToPlayer(id, client.PlayerName)) ? null : "Removal is not permitted."
            });
        }

        // Owner of "every split child proto gets exactly one Split result". A refused Decouple/Undock is not answered where it is
        // refused (it has no operation id); the child proto follows on the same reliable-ordered channel and is answered here.
        // Every refusal is RecoveryRequired: the client already split locally, so it must reconnect rather than carry on desynced.
        [ThreadStatic] private static Guid _answeredSplit;

        private static void AnswerSplit(ClientStructure client, VesselProtoMsgData msgData, EconomyResult result)
        {
            if (_answeredSplit == msgData.EconomySplitOperationId) return;
            _answeredSplit = msgData.EconomySplitOperationId;
            result.Operation = EconomyOperation.Split; result.RequestId = msgData.EconomySplitOperationId; result.VesselId = msgData.VesselId;
            AgencyEconomyStore.SendResult(client, result);
        }

        private static void RejectSplit(ClientStructure client, VesselBaseMsgData message, string reason)
        {
            var msgData = message as VesselProtoMsgData;
            if (msgData == null || !AgencyEconomyStore.ToolingEnabled || msgData.EconomySplitOperationId == Guid.Empty || _answeredSplit == msgData.EconomySplitOperationId) return;
            PlaytestDiagnostics.Write("vessel.split.reject", () => $"{PlaytestDiagnostics.Client(client)} vessel={msgData.VesselId} operation={msgData.EconomySplitOperationId} reason={reason}");
            // A pending split left behind would pin its parent forever.
            try { AgencyVesselMap.CancelPendingSplits(client.UniqueIdentifier, client.ConnectionTime.Ticks, msgData.VesselId); }
            catch (Exception e) { LunaLog.Warning($"Could not cancel the pending split of {msgData.VesselId}: {e.Message}"); }
            AnswerSplit(client, msgData, new EconomyResult { Success = false, RecoveryRequired = true, Reason = reason });
        }

        private static void HandleVesselProtoAnswered(ClientStructure client, VesselBaseMsgData message)
        {
            var msgData = (VesselProtoMsgData)message;
            _answeredSplit = Guid.Empty;
            try { HandleVesselProto(client, message); }
            catch (Exception e)
            {
                // A failed send must not mask the original exception.
                try { RejectSplit(client, msgData, "Split failed on the server: " + e.Message); }
                catch (Exception sendFailure) { LunaLog.Warning($"Could not answer the failed split of {msgData.VesselId}: {sendFailure.Message}"); }
                throw;
            }
            // Any return path that did not answer an operation-carrying proto still must.
            RejectSplit(client, msgData, "Split was rejected by the server.");
        }

        private static void HandleVesselProto(ClientStructure client, VesselBaseMsgData message)
        {
            var msgData = (VesselProtoMsgData)message;

            if (AgencyVesselMap.IsDeleted(msgData.VesselId) || VesselContext.RemovedVessels.ContainsKey(msgData.VesselId))
            {
                PlaytestDiagnostics.Write("vessel.proto.reject", () => $"{PlaytestDiagnostics.Client(client)} vessel={msgData.VesselId} reason=removed");
                RejectSplit(client, msgData, "Split vessel was removed.");
                return;
            }

            if (msgData.NumBytes == 0)
            {
                PlaytestDiagnostics.Write("vessel.proto.reject", () => $"{PlaytestDiagnostics.Client(client)} vessel={msgData.VesselId} reason=empty");
                LunaLog.Warning($"Received a vessel with 0 bytes ({msgData.VesselId}) from {client.PlayerName}.");
                RejectSplit(client, msgData, "Split vessel data was empty.");
                return;
            }

            var vesselText = Encoding.UTF8.GetString(msgData.Data, 0, msgData.NumBytes);
            var completedSplitParent = Guid.Empty;
            if(AgencyVesselMap.AgencyRulesActive)
            {
                lock(AgencyVesselMap.TransactionGate)
                {
                    if(!AgencyVesselMap.Ready || AgencyEconomyStore.ToolingEnabled && !AgencyEconomyStore.Ready || AgencyVesselMap.IsAbsorbed(msgData.VesselId) || AgencyVesselMap.IsDeleted(msgData.VesselId) || VesselContext.RemovedVessels.ContainsKey(msgData.VesselId)) { RejectSplit(client, msgData, "Split vessel is unavailable."); return; }
                    global::Server.System.Vessel.Classes.Vessel parsed;
                    try { parsed=new global::Server.System.Vessel.Classes.Vessel(vesselText); } catch { RejectSplit(client, msgData, "Split vessel data is invalid."); return; }
                    if(!Guid.TryParse(parsed.Fields.GetSingle("pid")?.Value,out var parsedId) || parsedId!=msgData.VesselId) { RejectSplit(client, msgData, "Split vessel id does not match."); return; }
                    // The decouple/undock announcement was refused (parent deleted, not controlled, boundary unknown), so this child has no pending split. Fail it here, not as a launch.
                    if (AgencyEconomyStore.ToolingEnabled && msgData.EconomySplitOperationId != Guid.Empty && AgencyVesselMap.PendingSplitParent(msgData.VesselId) == Guid.Empty) { RejectSplit(client, msgData, "Split was not accepted by the server."); return; }
                    if (!VesselStoreSystem.VesselExists(msgData.VesselId) && !AgencyEconomyStore.ValidateTradeEntitlement(client.AgencyId, msgData.TradeEntitlementId, parsed)) { RejectSplit(client, msgData, "Split vessel is not entitled."); return; }
                    if(global::Server.Settings.Structures.GeneralSettings.SettingsStore.ModControl && parsed.Parts.GetAllValues().Select(p=>p.Fields.GetSingle("name").Value).Except(ModFileSystem.ModControl.AllowedParts).Any()) { RejectSplit(client, msgData, "Split vessel uses disallowed parts."); return; }
                    if (AgencyEconomyStore.ToolingEnabled && AgencyVesselMap.IsSplitParent(msgData.VesselId)) { RejectSplit(client, msgData, "Split vessel is already a split parent."); return; }
                    var topologyChild=AgencyVesselMap.IsPendingSplit(msgData.VesselId) || AgencyVesselMap.Get(msgData.VesselId)!=null;
                    if (AgencyEconomyStore.ToolingEnabled && (VesselStoreSystem.VesselExists(msgData.VesselId) || AgencyVesselMap.IsPendingSplit(msgData.VesselId)) && !AgencyEconomyStore.UpdateCargoBindings(msgData.VesselId, AgencyVesselMap.PartIds(parsed), msgData.EconomyCargo)) { RejectSplit(client, msgData, "Split cargo does not match."); return; }
                    var splitParent = AgencyVesselMap.PendingSplitParent(msgData.VesselId);
                    if (splitParent != Guid.Empty && !AgencyVesselMap.SplitBelongsTo(msgData.VesselId, client.UniqueIdentifier, client.ConnectionTime.Ticks)) { RejectSplit(client, msgData, "Split belongs to another session."); return; }
                    if (splitParent != Guid.Empty && !VesselOwnershipSystem.CanControl(client, splitParent)) { RejectSplit(client, msgData, "Split parent is not controlled."); return; }
                    if (AgencyEconomyStore.ToolingEnabled && splitParent != Guid.Empty && msgData.EconomySplitOperationId == Guid.Empty) { RejectSplit(client, msgData, "Split operation is missing."); return; }
                    if(!AgencyVesselMap.ResolveSplit(msgData.VesselId,AgencyVesselMap.PartIds(parsed),vesselText,msgData.EconomySplitParentData.Length == 0 ? null : Encoding.UTF8.GetString(msgData.EconomySplitParentData)))
                    {
                        AgencyVesselMap.CancelPendingSplits(client.UniqueIdentifier, client.ConnectionTime.Ticks, msgData.VesselId);
                        RejectSplit(client, msgData, "Split topology does not match authoritative participants.");
                        return;
                    }
                    if (AgencyEconomyStore.ToolingEnabled && splitParent != Guid.Empty) completedSplitParent = splitParent;
                    var existing=VesselStoreSystem.VesselExists(msgData.VesselId);
                    if (AgencyEconomyStore.ToolingEnabled && !existing && !topologyChild)
                    {
                        var registration = msgData.EconomyParentVesselId != Guid.Empty ? AgencyEconomyStore.RegisterEva(client, msgData, vesselText, parsed) : AgencyEconomyStore.Register(client, msgData, vesselText, parsed);
                        AgencyEconomyStore.SendResult(client, registration);
                        if (!registration.Success)
                        {
                            PlaytestDiagnostics.Write("vessel.proto.reject", () => $"{PlaytestDiagnostics.Client(client)} vessel={msgData.VesselId} reason=registration detail={registration.Reason}");
                            LunaLog.Warning($"Dropped vessel {msgData.VesselId} from {client.PlayerName}: registration rejected ({registration.Reason}).");
                            return;
                        }
                    }
                    if(!existing) AgencyVesselMap.RegisterNew(msgData.VesselId,client.AgencyId);
                    if (AgencyEconomyStore.ToolingEnabled && !AgencyEconomyStore.ValidatePublishedParts(msgData.VesselId, AgencyVesselMap.PartIds(parsed))) { RejectSplit(client, msgData, "Split parts do not match the published craft."); return; }
                    // At the actual store write: a crew name leaving a stored craft may be about to board EVA.
                    if (existing) AgencyEconomyStore.RecordCrewDepartures(client, msgData.VesselId, parsed);
                    VesselStoreSystem.CurrentVessels[msgData.VesselId]=parsed;
                    if(!existing && !topologyChild)
                    {
                        var agency=AgencySystem.GetAgency(client.AgencyId);
                        if(agency!=null) { bool counted;lock(agency.Lock) { counted=agency.CountedVesselIds.Add(msgData.VesselId);if(counted) agency.VesselsLaunched++; } if(counted) { AgencyStore.PersistAgency(agency);AgencyNetwork.BroadcastUpsert(agency); } }
                        CraftCreationAndRemovalLog.LogCreated(msgData.VesselId,CraftCreationAndRemovalLog.ExtractVesselName(vesselText),client.PlayerName,msgData.Reason);
                    }
                }
                AgencyNetwork.BroadcastVesselMapEntry(msgData.VesselId,AgencyVesselMap.Get(msgData.VesselId)?.OwnerAgencyId??Guid.Empty);
                if (completedSplitParent != Guid.Empty)
                {
                    var parent = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
                    parent.VesselId = completedSplitParent;
                    parent.ForceReload = true;
                    parent.Data = msgData.EconomySplitParentData;
                    parent.NumBytes = parent.Data.Length;
                    parent.Reason = "Authoritative split parent";
                    parent.EconomyLaunchId = parent.EconomyLaunchToken = parent.EconomyParentVesselId = parent.EconomySplitOperationId = Guid.Empty;
                    parent.EconomyEvaCrew = null;
                    parent.EconomyCargo = null;
                    parent.EconomyManifestIndices = Array.Empty<int>();
                    parent.EconomySplitParentData = Array.Empty<byte>();
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, parent);
                    msgData.ForceReload = true;
                }
                MessageQueuer.RelayMessage<VesselSrvMsg>(client,msgData);
                if (AgencyEconomyStore.ToolingEnabled)
                {
                    AgencyEconomyStore.Broadcast();
                    if (msgData.EconomySplitOperationId != Guid.Empty) AnswerSplit(client, msgData, new EconomyResult { Success = true });
                }
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

            VesselDataUpdater.RawConfigNodeInsertOrUpdate(msgData.VesselId, vesselText, isNewVessel);
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

            if(VesselOwnershipSystem.Enabled || AgencyEconomyStore.ToolingEnabled)
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

            // The central service audit-logs the removal (name resolved before the store entry goes), keeps the ownership
            // map in sync and tells all clients, the sender included, to remove the weak vessel.
            VesselRemovalService.Remove(new[] { msgData.CoupledVesselId }, "Coupled/Docked", VesselRemovalMode.Ordinary, client, new VesselRemovalOptions { Target = VesselRemoveTarget.AllClients });
        }
    }
}
