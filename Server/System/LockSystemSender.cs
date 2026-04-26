using LmpCommon.Locks;
using LmpCommon.Message.Data.Lock;
using LmpCommon.Message.Server;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.Settings.Structures;
using System.Linq;

namespace Server.System
{
    public class LockSystemSender
    {
        public static void SendAllLocks(ClientStructure client)
        {
            var allLocks = LockSystem.LockQuery.GetAllLocks();

            // Per-agency contract pool: hide Contract locks held by players
            // outside this client's agency, so its agency-mate sees the
            // Contract lock as available and can claim it.
            if (GeneralSettings.SettingsStore.AgencyContractsPoolPerAgency
                && client != null && client.AgencyId != global::System.Guid.Empty)
            {
                allLocks = allLocks.Where(l =>
                {
                    if (l.Type != LockType.Contract) return true;
                    var holder = ClientRetriever.GetClientByName(l.PlayerName);
                    return holder == null || holder.AgencyId == client.AgencyId;
                });
            }

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<LockListReplyMsgData>();
            msgData.Locks = allLocks.ToArray();
            msgData.LocksCount = msgData.Locks.Length;

            MessageQueuer.SendToClient<LockSrvMsg>(client, msgData);
        }

        public static void ReleaseAndSendLockReleaseMessage(ClientStructure client, LockDefinition lockDefinition)
        {
            var lockReleaseResult = LockSystem.ReleaseLock(lockDefinition);
            if (lockReleaseResult)
            {
                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<LockReleaseMsgData>();
                msgData.Lock = lockDefinition;
                msgData.LockResult = true;

                // Mirror the acquire scoping: per-agency Contract locks only
                // notify the agency members about a release.
                if (GeneralSettings.SettingsStore.AgencyContractsPoolPerAgency
                    && lockDefinition.Type == LockType.Contract
                    && client != null && client.AgencyId != global::System.Guid.Empty)
                {
                    MessageQueuer.RelayMessageToAgency<LockSrvMsg>(client, client.AgencyId, msgData);
                }
                else
                {
                    MessageQueuer.RelayMessage<LockSrvMsg>(client, msgData);
                }
                LunaLog.Debug($"{lockDefinition.PlayerName} released lock {lockDefinition}");
            }
            else
            {
                SendStoredLockData(client, lockDefinition);
                LunaLog.Debug($"{lockDefinition.PlayerName} failed to release lock {lockDefinition}");
            }
        }

        public static void SendLockAcquireMessage(ClientStructure client, LockDefinition lockDefinition, bool force)
        {
            if (LockSystem.AcquireLock(lockDefinition, force, out var repeatedAcquire))
            {
                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<LockAcquireMsgData>();
                msgData.Lock = lockDefinition;
                msgData.Force = force;

                // Per-agency contract lock: only members of the acquiring
                // player's agency hear about it. Other agencies stay
                // blissfully unaware so their first member to ask can grab
                // the lock for their own pool.
                if (GeneralSettings.SettingsStore.AgencyContractsPoolPerAgency
                    && lockDefinition.Type == LockType.Contract
                    && client != null && client.AgencyId != global::System.Guid.Empty)
                {
                    MessageQueuer.SendMessageToAgency<LockSrvMsg>(client.AgencyId, msgData);
                }
                else
                {
                    MessageQueuer.SendToAllClients<LockSrvMsg>(msgData);
                }

                //Just log it if we actually changed the value. Users might send repeated acquire locks as they take a bit of time to reach them...
                if (!repeatedAcquire)
                    LunaLog.Debug($"{lockDefinition.PlayerName} acquired lock {lockDefinition}");
            }
            else
            {
                SendStoredLockData(client, lockDefinition);
                LunaLog.Debug($"{lockDefinition.PlayerName} failed to acquire lock {lockDefinition}");
            }
        }

        /// <summary>
        /// Whenever a release/acquire lock fails, call this method to relay the correct lock definition to the player
        /// </summary>
        private static void SendStoredLockData(ClientStructure client, LockDefinition lockDefinition)
        {
            var storedLockDef = LockSystem.LockQuery.GetLock(lockDefinition.Type, lockDefinition.PlayerName, lockDefinition.VesselId, lockDefinition.KerbalName);
            if (storedLockDef != null)
            {
                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<LockAcquireMsgData>();
                msgData.Lock = storedLockDef;
                MessageQueuer.SendToClient<LockSrvMsg>(client, msgData);
            }
        }
    }
}
