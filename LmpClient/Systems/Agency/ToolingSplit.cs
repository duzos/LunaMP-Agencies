using System;
using System.Collections.Generic;
using LmpClient.Network;
using LmpClient.Systems.TimeSync;
using LmpClient.Systems.VesselLockSys;
using LmpClient.VesselUtilities;
using LmpCommon.Agency;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Vessel;

namespace LmpClient.Systems.Agency
{
    public static partial class ToolingClient
    {
        private sealed class PendingSplit
        {
            internal Guid Operation, Parent, Child;
            internal DateTime Deadline;
            internal byte[] ParentData, ChildData;
            internal ToolingCargo[] Cargo;
            internal uint Part, RootPart;
            internal float Force;
            internal bool Undock;
            internal string Name;
            internal int VesselType;
        }
        private static PendingSplit splitting;
        private static readonly Queue<PendingSplit> splitQueue = new Queue<PendingSplit>();
        private static long splitBytes;
        private const string SplitLock = "LMP_ToolingSplit";
        internal static bool HasSplitPending => splitting != null;
        internal static void CaptureSplit(Vessel parent, Vessel child, uint part, float force, DockedVesselInfo info = null)
        {
            try
            {
                if (!parent || !child || parent.id == child.id) throw new InvalidOperationException("Invalid split participants.");
                var next = new PendingSplit { Operation = Guid.NewGuid(), Parent = parent.id, Child = child.id, Part = part, Force = force,
                    Undock = info != null, Name = info?.name, RootPart = info?.rootPartUId ?? 0, VesselType = info == null ? 0 : (int)info.vesselType };
                if (splitting == null)
                {
                    if (!VesselPublicationGuard.Begin(next.Operation, next.Parent, next.Child)) throw new InvalidOperationException("Another topology transaction is pending.");
                    InputLockManager.SetControlLock(VesselLockSystem.BlockAllControls, SplitLock);
                }
                var childProto = child.BackupVessel();
                next.ChildData = SerializeTopology(childProto);
                next.ParentData = SerializeTopology(parent.BackupVessel());
                next.Cargo = ToolingManifestBuilder.CaptureCargo(childProto);
                var size = next.ChildData.Length + next.ParentData.Length;
                if (splitQueue.Count >= 64 || splitBytes + size > 64L * 1024 * 1024) throw new InvalidOperationException("Too many pending split snapshots.");
                splitBytes += size;
                if (splitting == null) { splitting = next; SendSplit(); }
                else splitQueue.Enqueue(next);
            }
            catch (Exception e) { RecoveryDisconnect("Cannot confirm split: " + e.Message); }
        }
        private static void SendSplit()
        {
            var current = splitting;
            current.Deadline = DateTime.UtcNow.AddSeconds(45);
            VesselBaseMsgData announcement;
            if (current.Undock)
            {
                var data = NetworkMain.CliMsgFactory.CreateNewMessageData<VesselUndockMsgData>();
                data.NewVesselId = current.Child; data.PartFlightId = current.Part;
                data.DockedInfoName = current.Name; data.DockedInfoRootPartUId = current.RootPart; data.DockedInfoVesselType = current.VesselType;
                announcement = data;
            }
            else
            {
                var data = NetworkMain.CliMsgFactory.CreateNewMessageData<VesselDecoupleMsgData>();
                data.NewVesselId = current.Child; data.PartFlightId = current.Part; data.BreakForce = current.Force;
                announcement = data;
            }
            announcement.VesselId = current.Parent; announcement.GameTime = TimeSyncSystem.UniversalTime;
            NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<VesselCliMsg>(announcement));
            var proto = NetworkMain.CliMsgFactory.CreateNewMessageData<VesselProtoMsgData>();
            proto.VesselId = current.Child; proto.GameTime = TimeSyncSystem.UniversalTime; proto.ForceReload = true; proto.Reason = "Confirmed local split";
            proto.Data = current.ChildData; proto.NumBytes = current.ChildData.Length;
            proto.EconomyLaunchId = proto.EconomyLaunchToken = proto.EconomyParentVesselId = Guid.Empty;
            proto.EconomyEvaCrew = null; proto.EconomyManifestIndices = Array.Empty<int>();
            proto.EconomyCargo = current.Cargo; proto.EconomySplitOperationId = current.Operation; proto.EconomySplitParentData = current.ParentData;
            NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<VesselCliMsg>(proto));
        }
        private static byte[] SerializeTopology(ProtoVessel vessel)
        {
            var buffer = new byte[VesselOwnershipPolicy.MaxMergedVesselBytes];
            VesselSerializer.SerializeVesselToArray(vessel, buffer, out var count);
            if (count <= 0 || count > buffer.Length) throw new InvalidOperationException("Cannot serialize final craft topology.");
            var bytes = new byte[count]; Array.Copy(buffer, bytes, count); return bytes;
        }
        private static bool HandleSplit(EconomyResult result)
        {
            if (splitting == null || result.Operation != EconomyOperation.Split || result.RequestId != splitting.Operation) return false;
            if (!result.Success || result.RecoveryRequired) { RecoveryDisconnect(result.Reason); return true; }
            var accepted = splitting;
            splitBytes -= accepted.ChildData.Length + accepted.ParentData.Length;
            if (splitQueue.Count > 0)
            {
                splitting = splitQueue.Dequeue();
                if (!VesselPublicationGuard.AdvanceSplit(accepted.Operation, splitting.Operation, splitting.Parent, splitting.Child))
                { RecoveryDisconnect("Split publication session changed."); return true; }
                SendSplit();
            }
            else
            {
                splitting = null;
                VesselPublicationGuard.Complete(accepted.Operation);
                InputLockManager.RemoveControlLock(SplitLock);
            }
            return true;
        }
    }
}
