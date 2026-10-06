using System;
using System.Linq;
using LmpClient.VesselUtilities;
using LmpClient.Systems.VesselLockSys;
using LmpCommon.Agency;

namespace LmpClient.Systems.Agency
{
    public static partial class ToolingClient
    {
        private sealed class PendingBoarding
        {
            internal Guid Operation, Eva, Target;
            internal string Crew;
            internal uint[] TargetParts;
            internal DateTime Deadline;
            internal bool Finished, Sent;
        }
        private static PendingBoarding boarding;
        private const string BoardingLock = "LMP_ToolingBoarding";
        internal static bool IsBoarding(Guid vessel) => boarding != null && boarding.Eva == vessel;
        internal static bool BeginBoarding(KerbalEVA eva, Part target, out bool entered)
        {
            entered = false;
            if (!Enabled || DockingCoordinator.Replaying) return true;
            if (!Ready || boarding != null || !eva || !target || !target.vessel || !eva.vessel ||
                eva.part.protoModuleCrew.Count != 1 || !AgencySystem.Singleton.CanControlVessel(eva.vessel.id) ||
                !AgencySystem.Singleton.CanControlVessel(target.vessel.id)) return false;
            var operation = Guid.NewGuid();
            if (!VesselPublicationGuard.Begin(operation)) return false;
            boarding = new PendingBoarding { Operation = operation, Eva = eva.vessel.id, Target = target.vessel.id,
                Crew = eva.part.protoModuleCrew[0].name, TargetParts = target.vessel.parts.Select(p => p.flightID).OrderBy(id => id).ToArray(),
                Deadline = DateTime.UtcNow.AddSeconds(45) };
            InputLockManager.SetControlLock(VesselLockSystem.BlockAllControls, BoardingLock);
            entered = true;
            return true;
        }
        internal static void EndBoarding(bool entered, Exception error)
        {
            if (!entered || boarding == null) return;
            if (error != null) { RecoveryDisconnect("Boarding interrupted; reload authoritative craft."); return; }
            boarding.Finished = true;
        }
        private static void TickBoarding()
        {
            if (boarding == null) return;
            if (DateTime.UtcNow > boarding.Deadline) { RecoveryDisconnect("Boarding confirmation timed out."); return; }
            if (!boarding.Finished || boarding.Sent) return;
            try
            {
                var vessel = FlightGlobals.FindVessel(boarding.Target);
                if (!vessel || !vessel.parts.Select(p => p.flightID).OrderBy(id => id).SequenceEqual(boarding.TargetParts) ||
                    !vessel.GetVesselCrew().Any(c => c.name == boarding.Crew)) throw new InvalidOperationException("Boarding changed the target craft.");
                var buffer = new byte[VesselOwnershipPolicy.MaxMergedVesselBytes];
                VesselSerializer.SerializeVesselToArray(vessel.BackupVessel(), buffer, out var count);
                if (count <= 0 || count > buffer.Length) throw new InvalidOperationException("Cannot serialize boarded craft.");
                var bytes = new byte[count]; Array.Copy(buffer, bytes, count);
                boarding.Sent = true;
                Send(new EconomyCommand { RequestId = boarding.Operation, Operation = EconomyOperation.BoardEva,
                    VesselId = boarding.Eva, ParentVesselId = boarding.Target, CrewName = boarding.Crew, VesselData = bytes });
            }
            catch (Exception e) { RecoveryDisconnect(e.Message); }
        }
        private static bool HandleBoarding(EconomyResult result)
        {
            if (boarding == null || result.RequestId != boarding.Operation) return false;
            if (!result.Success || result.RecoveryRequired) { RecoveryDisconnect(result.Reason); return true; }
            var accepted = boarding;
            boarding = null;
            VesselPublicationGuard.Complete(accepted.Operation);
            InputLockManager.RemoveControlLock(BoardingLock);
            VesselCommon.RemoveVesselFromSystems(accepted.Eva);
            return true;
        }
    }
}
