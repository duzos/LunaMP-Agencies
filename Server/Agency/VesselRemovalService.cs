using LmpCommon.Locks;
using LmpCommon.Message.Data.Lock;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Agency
{
    /// <summary>Who receives the VesselRemove message.</summary>
    public enum VesselRemoveTarget
    {
        /// <summary>Everyone except the initiating client (client-originated removals).</summary>
        OtherClients,
        /// <summary>Every connected client, the initiator included (stock couple, server cleanups, deletion).</summary>
        AllClients
    }

    public sealed class VesselRemovalOptions
    {
        /// <summary>Kill-list the ids (and mark journalled removals permanent).</summary>
        public bool Permanent;
        /// <summary>AddToKillList on the VesselRemove message sent to clients.</summary>
        public bool ClientKillList;
        /// <summary>Admin cleanup: when agency rules are active, skip pending-split, split-parent and controlled craft.</summary>
        public bool AdminFilters;
        /// <summary>Evaluated per id under the transaction gate; returns a denial reason, or null to allow.</summary>
        public Func<Guid, string> Authorize;
        public VesselRemoveTarget Target = VesselRemoveTarget.AllClients;
        /// <summary>The client's own message, relayed unchanged (KillOnReceive and all) instead of a rebuilt one.</summary>
        public VesselRemoveMsgData Original;

        /// <summary>Dekessler, nuke and clearvessels: memory-only kill list when agency rules are active (none in pure stock mode).</summary>
        public static VesselRemovalOptions AdminCleanup(Func<Guid, string> authorize = null)
            => new VesselRemovalOptions { Permanent = AgencyVesselMap.AgencyRulesActive, AdminFilters = true, Authorize = authorize, Target = VesselRemoveTarget.AllClients };
    }

    public sealed class VesselRemovalResult
    {
        /// <summary>False only when the removal itself failed; denied ids do not make a removal fail.</summary>
        public bool Success = true;
        public string Reason = string.Empty;
        public Guid[] Removed = Array.Empty<Guid>();
        public readonly Dictionary<Guid, string> Denied = new Dictionary<Guid, string>();
    }

    /// <summary>
    /// The one path that removes vessels: ownership and payment provenance, the store entry and its file, locks, then broadcasts.
    /// Every remover (client remove, stock couple, dekessler, nuke, clearvessels, owner deletion) goes through here.
    /// </summary>
    public static class VesselRemovalService
    {
        public static VesselRemovalResult Remove(IEnumerable<Guid> ids, string reason, VesselRemovalMode mode, ClientStructure initiator, VesselRemovalOptions options)
        {
            options = options ?? new VesselRemovalOptions();
            var result = new VesselRemovalResult();
            var released = new List<LockDefinition>();
            var names = new Dictionary<Guid, string>();
            var mapEntries = new List<Guid>();
            bool rules;
            lock (AgencyVesselMap.TransactionGate)
            {
                rules = AgencyVesselMap.AgencyRulesActive;
                // 1. Filter ids.
                var accepted = new List<Guid>();
                foreach (var id in ids.Where(i => i != Guid.Empty).Distinct())
                {
                    var denial = options.Authorize?.Invoke(id);
                    if (denial == null && options.AdminFilters && rules &&
                        (AgencyVesselMap.IsPendingSplit(id) || AgencyVesselMap.IsSplitParent(id) || LockSystem.LockQuery.ControlLockExists(id)))
                        denial = "Craft is busy.";
                    if (denial != null) result.Denied[id] = denial; else accepted.Add(id);
                }
                result.Removed = accepted.ToArray();
                if (accepted.Count == 0) return result;
                foreach (var id in accepted)
                {
                    if (VesselStoreSystem.CurrentVessels.TryGetValue(id, out var vessel)) names[id] = TryName(vessel);
                    if (names.ContainsKey(id) || AgencyVesselMap.Get(id) != null) mapEntries.Add(id);
                }
                // 2. Kill-list before touching the store, so a proto task already in flight observes it.
                var permanent = options.Permanent || mode == VesselRemovalMode.Deleted;
                var killed = new List<Guid>();
                if (permanent) foreach (var id in accepted) if (VesselContext.RemovedVessels.TryAdd(id, 0)) killed.Add(id);
                // 3. One ownership commit for the whole pass.
                var epoch = AgencyVesselMap.CaptureEpoch();
                if (AgencyVesselMap.MapMaintained && !rules && !AgencyVesselMap.Ready)
                    // Pure stock mode must not lose upstream removals because the bookkeeping map failed to load.
                    LunaLog.Warning($"[Agency] Ownership map unavailable; removing without bookkeeping ({reason}).");
                else if (AgencyVesselMap.MapMaintained)
                {
                    try { AgencyVesselMap.RemoveMany(accepted, mode, permanent); }
                    catch (Exception e)
                    {
                        // Nothing durable happened: restore the kill list. Once a journal or commit exists, recovery owns the store state.
                        var committed = AgencyVesselMap.CaptureEpoch() != epoch || AgencyVesselMap.HasPendingJournal || AgencyEconomyStore.HasPendingJournal;
                        if (!committed) foreach (var id in killed) VesselContext.RemovedVessels.TryRemove(id, out _);
                        LunaLog.Error($"[Agency] Vessel removal failed ({reason}): {e.GetType().Name}: {e.Message}");
                        result.Success = false; result.Reason = e.Message; result.Removed = Array.Empty<Guid>();
                        return result;
                    }
                }
                // 4. The file and the store entry go synchronously, as RecoverProjection does.
                foreach (var id in accepted) DeleteStored(id);
                // 5. Locks are released inside the gate; LockSystem uses the same re-entrant gate.
                var idSet = new HashSet<Guid>(accepted);
                foreach (var held in LockSystem.LockQuery.GetAllLocks().Where(l => idSet.Contains(l.VesselId)).ToArray())
                    if (LockSystem.ReleaseLock(held)) released.Add(held);
            }

            foreach (var held in released)
            {
                var release = ServerContext.ServerMessageFactory.CreateNewMessageData<LockReleaseMsgData>();
                release.Lock = held; release.LockResult = true;
                MessageQueuer.SendToAllClients<LockSrvMsg>(release);
            }
            foreach (var id in result.Removed)
            {
                if (initiator != null && names.ContainsKey(id)) CraftCreationAndRemovalLog.LogRemoved(id, names[id], initiator.PlayerName, reason);
                var message = options.Original != null && result.Removed.Length == 1 ? options.Original : Build(id, reason, options.ClientKillList);
                if (options.Target == VesselRemoveTarget.OtherClients && initiator != null) MessageQueuer.RelayMessage<VesselSrvMsg>(initiator, message);
                else MessageQueuer.SendToAllClients<VesselSrvMsg>(message);
            }
            if (rules)
            {
                // Changed() resyncs the map, CommNet, visibility and economy once for the whole pass.
                VesselOwnershipSystem.Changed();
            }
            else
            {
                // Stock mode: light messages only, no full snapshots.
                foreach (var id in mapEntries) AgencyNetwork.BroadcastVesselMapEntry(id, Guid.Empty);
                AgencyCommNetStore.Broadcast();
                AgencyVisibilityStore.Broadcast();
            }
            return result;
        }

        private static VesselRemoveMsgData Build(Guid id, string reason, bool killList)
        {
            var message = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselRemoveMsgData>();
            message.VesselId = id; message.AddToKillList = killList; message.Reason = reason;
            return message;
        }

        private static void DeleteStored(Guid id)
        {
            try { var file = Path.Combine(VesselStoreSystem.VesselsPath, id + VesselStoreSystem.VesselFileFormat); if (File.Exists(file)) File.Delete(file); }
            catch (Exception e) { LunaLog.Warning($"[Agency] Could not delete vessel file {id}: {e.Message}"); }
            finally { VesselStoreSystem.CurrentVessels.TryRemove(id, out _); }
        }

        private static string TryName(global::Server.System.Vessel.Classes.Vessel vessel)
        {
            try { return vessel.Fields.GetSingle("name")?.Value; }
            catch { return null; }
        }
    }
}
