using Server.Diagnostics;
using LmpCommon.Locks;
using Server.Client;
using Server.Settings.Structures;
using System.Linq;

namespace Server.System
{
    public class LockSystem
    {
        private static readonly LockStore LockStore = new LockStore();
        public static readonly LockQuery LockQuery = new LockQuery(LockStore);

        public static bool AcquireLock(LockDefinition lockDef, bool force, out bool repeatedAcquire)
        {
            var gate = global::Server.Agency.AgencyVesselMap.TransactionGate;
            var callerHoldsGate = global::System.Threading.Monitor.IsEntered(gate);
            bool accepted, wasHeld;
            lock(gate)
            {
                wasHeld = LockQuery.LockExists(lockDef);
                accepted = AcquireLocked(lockDef,force,out repeatedAcquire);
            }
            // A coordinating caller emits its own result after leaving the outer gate.
            if (!callerHoldsGate)
            {
                var result = accepted ? (repeatedAcquire ? "repeated" : "accepted") : "denied";
                var reason = accepted ? "" : wasHeld ? "held" : "permission";
                PlaytestDiagnostics.Write("lock.acquire", () => $"type={lockDef.Type} vessel={lockDef.VesselId} result={result} reason={reason} force={force}");
            }
            return accepted;
        }

        private static bool AcquireLocked(LockDefinition lockDef,bool force,out bool repeatedAcquire)
        {
            repeatedAcquire = false;
            if (lockDef.Type == LockType.Control && global::Server.Agency.VesselOwnershipSystem.Enabled && !global::Server.Agency.VesselOwnershipSystem.CanControl(ClientRetriever.GetClientByName(lockDef.PlayerName), lockDef.VesselId)) return false;

            //Player tried to acquire a lock that they already own
            if (LockQuery.LockBelongsToPlayer(lockDef.Type, lockDef.VesselId, lockDef.KerbalName, lockDef.PlayerName))
            {
                repeatedAcquire = true;
                return true;
            }

            // When the per-agency contracts pool is on, multiple players can
            // hold a Contract lock simultaneously — each agency gets its own
            // pool generator. Standard exclusivity is skipped for this lock
            // type. The agency-scoped relay in LockSystemSender ensures
            // other agencies are never told about it (so their clients keep
            // thinking they can also acquire).
            var contractsPerAgency = GeneralSettings.SettingsStore.AgencyContractsPoolPerAgency
                                     && lockDef.Type == LockType.Contract;

            if (force || contractsPerAgency || !LockQuery.LockExists(lockDef))
            {
                if (lockDef.Type == LockType.Control)
                {
                    //If they acquired a control lock they probably switched vessels or something like that and they can only have one control lock.
                    //So remove the other control locks just for safety...
                    var controlLocks = LockQuery.GetAllPlayerLocks(lockDef.PlayerName).Where(l => l.Type == LockType.Control);
                    foreach (var control in controlLocks)
                        ReleaseLock(control);
                }

                LockStore.AddOrUpdateLock(lockDef);
                return true;
            }
            return false;
        }

        public static bool ReleaseLock(LockDefinition lockDef)
        {
            lock(global::Server.Agency.AgencyVesselMap.TransactionGate) return ReleaseLocked(lockDef);
        }

        private static bool ReleaseLocked(LockDefinition lockDef)
        {
            if (LockQuery.LockBelongsToPlayer(lockDef.Type, lockDef.VesselId, lockDef.KerbalName, lockDef.PlayerName))
            {
                LockStore.RemoveLock(lockDef);
                return true;
            }

            return false;
        }

        public static void ReleasePlayerLocks(ClientStructure client)
        {
            var removeList = LockQuery.GetAllPlayerLocks(client.PlayerName);

            foreach (var lockToRemove in removeList)
            {
                LockSystemSender.ReleaseAndSendLockReleaseMessage(client, lockToRemove);
            }
        }
    }
}
