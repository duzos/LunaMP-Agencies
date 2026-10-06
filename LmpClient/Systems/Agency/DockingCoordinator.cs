using System;
using System.Collections.Generic;
using System.Linq;
using LmpClient.Network;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselCoupleSys;
using LmpClient.Systems.VesselLockSys;
using LmpClient.Systems.VesselProtoSys;
using LmpClient.Systems.VesselRemoveSys;
using LmpClient.VesselUtilities;
using LmpCommon.Agency;
using LmpCommon.Enums;
using UnityEngine;

namespace LmpClient.Systems.Agency
{
    public static class DockingCoordinator
    {
        [ThreadStatic] private static int replayDepth;
        [ThreadStatic] private static int localDepth;
        public static bool Replaying => replayDepth > 0;
        public static bool InDocking => localDepth > 0;
        private static PendingCouple pending;
        private static readonly Dictionary<string, DateTime> requests = new Dictionary<string, DateTime>();
        private const string InputLock = "LMP_AgencyDockPending";
        private sealed class PendingCouple
        {
            public Guid OperationId, GrantId, Source, Target, Survivor, Removed;
            public DateTime Deadline;
            public uint SurvivorPart, RemovedPart;
            public CoupleTrigger Trigger;
            public bool PhysicalComplete, Sent, DeferSerialization;
            public int ExpectedParts;
        }
        public static IDisposable Replay()
        {
            replayDepth++;
            return new ReplayScope();
        }
        private sealed class ReplayScope : IDisposable { public void Dispose() { replayDepth--; } }

        internal static bool Enter(Part a, Part b, CoupleTrigger trigger, out bool entered, bool beginTransaction = true)
        {
            entered = false;
            if (!AgencySystem.OwnershipEnabled || Replaying) return true;
            if (!a || !b || !a.vessel || !b.vessel) return false;
            if (a.vessel.id == b.vessel.id)
            {
                if (!beginTransaction) { localDepth++; entered = true; }
                return true;
            }
            if (!Harmony.AgencyVesselDocking.Ready) return false;
            if (pending != null)
            {
                if (!SamePair(a.vessel.id, b.vessel.id, pending.Source, pending.Target) || pending.Sent) { Abort("Another topology change occurred during docking confirmation."); return false; }
                localDepth++; entered = true; return true;
            }
            if (VesselPublicationGuard.Pending || VesselCommon.IsSpectating) return false;
            var player = SettingsSystem.CurrentSettings.PlayerName;
            var source = LockSystem.LockQuery.ControlLockBelongsToPlayer(a.vessel.id, player) ? a.vessel :
                LockSystem.LockQuery.ControlLockBelongsToPlayer(b.vessel.id, player) ? b.vessel : null;
            if (!source || !AgencySystem.Singleton.CanControlVessel(source.id)) return false;
            var target = source == a.vessel ? b.vessel : a.vessel;
            if (!AgencySystem.Singleton.OwnershipReady) return false;
            var ownership = AgencySystem.Singleton.GetOwnershipSnapshot();
            ownership.TryGetValue(target.id, out var targetOwner);
            var generic = trigger == CoupleTrigger.Kerbal || trigger == CoupleTrigger.Other;
            if (generic && !AgencySystem.Singleton.CanControlVessel(target.id)) return false;
            var implicitPermission = generic || targetOwner == null || targetOwner.OwnerAgencyId == Guid.Empty || targetOwner.OwnerAgencyId == AgencySystem.Singleton.MyAgencyId;
            var grant = AgencySystem.Singleton.GetDockRequests().FirstOrDefault(r => r.Status == DockConsentStatus.Granted && r.SourceVesselId == source.id && r.TargetVesselId == target.id && r.ExpiresUtcTicks > DateTime.UtcNow.Ticks);
            if (!implicitPermission && grant == null)
            {
                var key = source.id + ":" + target.id;
                if (!requests.TryGetValue(key, out var until) || until < DateTime.UtcNow)
                {
                    if (requests.Count >= 128) requests.Clear();
                    requests[key] = DateTime.UtcNow.AddSeconds(35);
                    AgencySystem.Singleton.MessageSender.RequestDock(source.id, target.id);
                    ScreenMessages.PostScreenMessage("Docking permission requested. Hold position while the owning agency responds.", 5f, ScreenMessageStyle.UPPER_CENTER);
                }
                return false;
            }
            // FSM transitions also include passive capture/swap states. They need permission gating,
            // but only the concrete DockToVessel/Grapple call starts a physical transaction.
            if (!beginTransaction) { localDepth++; entered = true; return true; }
            var operation = Guid.NewGuid();
            if (!VesselPublicationGuard.Begin(operation)) return false;
            pending = new PendingCouple { OperationId = operation, GrantId = generic ? Guid.Empty : grant?.GrantId ?? Guid.Empty, Source = source.id, Target = target.id,
                Deadline = DateTime.UtcNow.AddSeconds(30), Trigger = trigger, DeferSerialization = generic, ExpectedParts = source.parts.Count + target.parts.Count };
            InputLockManager.SetControlLock(VesselLockSystem.BlockAllControls, InputLock);
            localDepth++; entered = true;
            Diagnostics.PlaytestDiagnostics.Write("client.dock.begin", () => $"operation={operation} source={source.id} target={target.id}");
            return true;
        }
        internal static void Exit(bool entered, Exception error)
        {
            if (!entered) return;
            localDepth--;
            if (error != null && pending != null) { Abort("Docking interrupted before confirmation."); return; }
            if (localDepth != 0 || pending == null || pending.Sent) return;
            if (!pending.PhysicalComplete) { Abort("Docking did not complete locally. Reconnect to reload authoritative craft."); return; }
            if (pending.DeferSerialization) return;
            PublishMergedVessel();
        }
        private static void PublishMergedVessel()
        {
            try
            {
                var survivor = FlightGlobals.FindVessel(pending.Survivor);
                if (!survivor || survivor.parts.Count != pending.ExpectedParts) throw new InvalidOperationException("Merged vessel topology differs");
                var buffer = new byte[VesselOwnershipPolicy.MaxMergedVesselBytes];
                VesselSerializer.SerializeVesselToArray(survivor.BackupVessel(), buffer, out var count);
                if (count <= 0 || count > buffer.Length) throw new InvalidOperationException("Merged vessel could not be serialized");
                var bytes = new byte[count]; Array.Copy(buffer, bytes, count);
                pending.Sent = true;
                VesselCoupleSystem.Singleton.MessageSender.SendVesselCouple(survivor, pending.SurvivorPart, pending.Removed, pending.RemovedPart, pending.Trigger, pending.OperationId, pending.GrantId, bytes);
            }
            catch (Exception e) { Abort("Cannot confirm merged craft: " + e.Message); }
        }
        internal static bool BeforePartCouple(Part a, Part b, out bool entered)
        {
            entered = false;
            if (!AgencySystem.OwnershipEnabled || Replaying) return true;
            if (!a || !b || !a.vessel || !b.vessel) return false;
            if (a.vessel == b.vessel) return true;
            if (pending != null)
            {
                if (!SamePair(a.vessel.id, b.vessel.id, pending.Source, pending.Target)) { Abort("Unexpected coupling while a dock is pending."); return false; }
                pending.Survivor = b.vessel.id; pending.Removed = a.vessel.id;
                return true;
            }
            // Generic coupling still needs atomic publication, only between controllable craft.
            var eva = a.vessel.isEVA || b.vessel.isEVA;
            if (!eva && (a.FindModuleImplementing<ModuleDockingNode>() != null || b.FindModuleImplementing<ModuleDockingNode>() != null ||
                a.FindModuleImplementing<ModuleGrappleNode>() != null || b.FindModuleImplementing<ModuleGrappleNode>() != null)) return false;
            if (!Enter(a, b, eva ? CoupleTrigger.Kerbal : CoupleTrigger.Other, out entered)) return false;
            pending.Survivor = b.vessel.id; pending.Removed = a.vessel.id;
            return true;
        }
        public static bool DeferCouple(Part from, Part to, Guid removed)
        {
            if (!AgencySystem.OwnershipEnabled || Replaying || pending == null) return false;
            pending.Survivor = from.vessel.id; pending.Removed = removed;
            pending.SurvivorPart = to.flightID; pending.RemovedPart = from.flightID; pending.PhysicalComplete = true;
            return true;
        }
        public static bool BeforeDestroy(Vessel vessel)
        {
            if (Replaying || pending == null || !vessel) return true;
            if (vessel.id == pending.Removed) return false;
            if (vessel.id == pending.Survivor || vessel.id == pending.Source || vessel.id == pending.Target) { Abort("A docking participant was destroyed before confirmation."); return false; }
            return true;
        }
        public static bool BeforeTopologyChange()
        {
            if (Replaying || pending == null) return true;
            Abort("Craft changed while docking confirmation was pending."); return false;
        }
        internal static void HandleStatus(DockConsentSnapshot status)
        {
            if (status.Status == DockConsentStatus.Granted)
                ScreenMessages.PostScreenMessage("Docking approved. Approach again if needed before permission expires.", 5f, ScreenMessageStyle.UPPER_CENTER);
            if (pending == null || status.OperationId != pending.OperationId) return;
            if (status.Status == DockConsentStatus.Rejected || status.Status == DockConsentStatus.RecoveryRequired) { Abort(status.Reason ?? "Docking was rejected."); return; }
            if (status.Status != DockConsentStatus.Completed) return;
            var accepted = pending;
            pending = null;
            VesselPublicationGuard.Complete(accepted.OperationId);
            InputLockManager.RemoveControlLock(InputLock);
            VesselCoupleEvents.CompleteAccepted(accepted.Survivor, accepted.Removed, accepted.Trigger);
            var survivor = FlightGlobals.FindVessel(accepted.Survivor);
            if (survivor) VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(survivor, true, "Confirmed agency docking");
        }
        internal static void Tick()
        {
            if (pending == null) return;
            if (DateTime.UtcNow > pending.Deadline) { Abort("Docking confirmation timed out. Reconnect to reload the authoritative craft."); return; }
            if (pending.PhysicalComplete && !pending.Sent && localDepth == 0)
            {
                PublishMergedVessel();
                if (pending == null) return;
            }
            if (pending.PhysicalComplete)
            {
                var vessel = FlightGlobals.FindVessel(pending.Survivor);
                if (!vessel || vessel.parts.Count != pending.ExpectedParts) Abort("Docked craft changed before confirmation.");
            }
        }
        internal static void Clear()
        {
            pending = null; localDepth = 0; requests.Clear(); InputLockManager.RemoveControlLock(InputLock);
        }
        internal static void Abort(string reason)
        {
            VesselPublicationGuard.Reject();
            pending = null;
            Diagnostics.PlaytestDiagnostics.Write("client.dock.recovery", () => reason);
            NetworkConnection.Disconnect(reason);
            // Disconnect normally leaves flight running. Speculative topology must instead leave the universe.
            MainSystem.Singleton.ForceQuit = true;
        }
        private static bool SamePair(Guid a, Guid b, Guid c, Guid d) => (a == c && b == d) || (a == d && b == c);
    }
}


