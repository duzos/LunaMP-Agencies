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
            repeatedAcquire = false;

            //Player tried to acquire a lock that they already own
            if (LockQuery.LockBelongsToPlayer(lockDef.Type, lockDef.VesselId, lockDef.KerbalName, lockDef.PlayerName))
            {
                PlaytestDiagnostics.Write("lock.acquire", () => $"type={lockDef.Type} vessel={lockDef.VesselId} result=repeated force={force}", true);
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
                PlaytestDiagnostics.Write("lock.acquire", () => $"type={lockDef.Type} vessel={lockDef.VesselId} result=accepted force={force} contractsPerAgency={contractsPerAgency}");
                return true;
            }
            PlaytestDiagnostics.Write("lock.acquire", () => $"type={lockDef.Type} vessel={lockDef.VesselId} result=denied reason=held force={force}");
            return false;
        }

        public static bool ReleaseLock(LockDefinition lockDef)
        {
            if (LockQuery.LockBelongsToPlayer(lockDef.Type, lockDef.VesselId, lockDef.KerbalName, lockDef.PlayerName))
            {
                LockStore.RemoveLock(lockDef);
                PlaytestDiagnostics.Write("lock.release", () => $"type={lockDef.Type} vessel={lockDef.VesselId} result=accepted");
                return true;
            }

            PlaytestDiagnostics.Write("lock.release", () => $"type={lockDef.Type} vessel={lockDef.VesselId} result=denied reason=not-owner");
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
