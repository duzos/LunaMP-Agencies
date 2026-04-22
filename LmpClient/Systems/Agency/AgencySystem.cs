using LmpClient.Base;
using LmpCommon.Agency;
using LmpCommon.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// Client-side mirror of the server's agency state. The server is
    /// authoritative; this system only caches what the server tells it and
    /// forwards user-driven actions via <see cref="AgencyMessageSender"/>.
    /// </summary>
    public class AgencySystem : MessageSystem<AgencySystem, AgencyMessageSender, AgencyMessageHandler>
    {
        public override string SystemName { get; } = nameof(AgencySystem);

        /// <summary>Enable as soon as the player is authenticated so the
        /// initial <see cref="LmpCommon.Message.Data.Agency.AgencySyncAllMsgData"/>
        /// is processed before scenarios arrive.</summary>
        protected override ClientState EnableStage => ClientState.Handshaking;

        // MessageHandler is called from a non-Unity thread — our collections
        // are ConcurrentDictionary so we can process directly.
        protected override bool ProcessMessagesInUnityThread => false;

        public readonly ConcurrentDictionary<Guid, AgencyInfo> KnownAgencies = new ConcurrentDictionary<Guid, AgencyInfo>();
        public readonly List<JoinRequestInfo> PendingIncomingRequests = new List<JoinRequestInfo>();
        public readonly object RequestsLock = new object();

        public Guid MyAgencyId { get; set; } = Guid.Empty;

        /// <summary>Tuple list of (timestamp, message) for server replies,
        /// consumed by the AgencyWindow for transient UI toasts.</summary>
        public readonly ConcurrentQueue<string> PendingServerMessages = new ConcurrentQueue<string>();

        public AgencyInfo GetMyAgency()
        {
            if (MyAgencyId == Guid.Empty) return null;
            KnownAgencies.TryGetValue(MyAgencyId, out var a);
            return a;
        }

        public bool AmIOwnerOfMine()
        {
            var me = GetMyAgency();
            if (me == null) return false;
            return string.Equals(me.OwnerUniqueId, MainSystem.UniqueIdentifier, StringComparison.Ordinal);
        }

        protected override void OnEnabled()
        {
            base.OnEnabled();
            LunaLog.Log("[Agency] Client AgencySystem enabled.");
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();
            KnownAgencies.Clear();
            lock (RequestsLock) PendingIncomingRequests.Clear();
            MyAgencyId = Guid.Empty;
            LunaLog.Log("[Agency] Client AgencySystem disabled and cleared.");
        }
    }
}
