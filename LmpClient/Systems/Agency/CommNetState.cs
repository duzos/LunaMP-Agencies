using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using LmpCommon.Agency;
using LmpCommon.Message.Data.Agency;

namespace LmpClient.Systems.Agency
{
    public sealed class CommNetEndpointSnapshot
    {
        public Guid VesselId { get; }
        public Guid OwnerAgencyId { get; }
        public long OwnershipRevision { get; }
        internal readonly CommNetEndpoint Value;
        internal CommNetEndpointSnapshot(CommNetEndpoint value)
        {
            VesselId = value.VesselId; OwnerAgencyId = value.OwnerAgencyId; OwnershipRevision = value.OwnershipRevision;
            Value = new CommNetEndpoint { VesselId = VesselId, OwnerAgencyId = OwnerAgencyId, OwnershipRevision = OwnershipRevision };
        }
    }
    public sealed class CommNetPreferenceSnapshot
    {
        public CommNetEndpointSnapshot Source { get; }
        public bool AcceptAll { get; }
        public IReadOnlyList<CommNetEndpointSnapshot> Targets { get; }
        internal readonly CommNetPreference Value;
        internal CommNetPreferenceSnapshot(CommNetPreference value)
        {
            Source = new CommNetEndpointSnapshot(value.Source); AcceptAll = value.AcceptAll;
            Targets = Array.AsReadOnly(value.Targets.Select(t => new CommNetEndpointSnapshot(t)).ToArray());
            Value = new CommNetPreference { Source = Source.Value, AcceptAll = AcceptAll, Targets = Targets.Select(t => t.Value).ToArray() };
        }
    }
    public sealed class CommNetResultSnapshot
    {
        public Guid RequestId { get; internal set; }
        public bool Success { get; internal set; }
        public string Reason { get; internal set; }
    }
    public partial class AgencySystem
    {
        private readonly object commNetLock = new object();
        private Dictionary<Guid, CommNetEndpointSnapshot> commNetEndpoints = new Dictionary<Guid, CommNetEndpointSnapshot>();
        private Dictionary<Guid, CommNetPreferenceSnapshot> commNetPreferences = new Dictionary<Guid, CommNetPreferenceSnapshot>();
        private readonly CommNetPreference[] commNetPolicyPair = new CommNetPreference[2];
        private bool commNetReady;
        private long commNetRevision = -1;
        private CommNetResultSnapshot latestCommNetResult;
        public bool CommNetReady { get { lock (commNetLock) return commNetReady; } }
        public long CommNetRevision { get { lock (commNetLock) return commNetRevision; } }
        public CommNetResultSnapshot LatestCommNetResult { get { lock (commNetLock) return latestCommNetResult; } }
        public IReadOnlyDictionary<Guid, CommNetEndpointSnapshot> GetCommNetEndpoints()
        {
            lock (commNetLock) return new ReadOnlyDictionary<Guid, CommNetEndpointSnapshot>(commNetEndpoints);
        }
        public IReadOnlyDictionary<Guid, CommNetPreferenceSnapshot> GetCommNetPreferences()
        {
            lock (commNetLock) return new ReadOnlyDictionary<Guid, CommNetPreferenceSnapshot>(commNetPreferences);
        }
        internal bool CanLinkCommNet(Guid a, Guid b)
        {
            lock (commNetLock)
            {
                commNetEndpoints.TryGetValue(a, out var first); commNetEndpoints.TryGetValue(b, out var second);
                commNetPreferences.TryGetValue(a, out var firstPreference); commNetPreferences.TryGetValue(b, out var secondPreference);
                commNetPolicyPair[0] = firstPreference?.Value; commNetPolicyPair[1] = secondPreference?.Value;
                return CommNetOptInPolicy.CanLink(true, commNetReady, false, false, first?.Value, second?.Value, commNetPolicyPair);
            }
        }
        internal void ApplyCommNet(AgencyCommNetSnapshotMsgData data)
        {
            lock (commNetLock)
            {
                if (data.Revision < commNetRevision) return;
                // DTO decoding validates bounds and entries; copy all pooled arrays before publication.
                commNetReady = false;
                commNetEndpoints = (data.Endpoints ?? Array.Empty<CommNetEndpoint>()).ToDictionary(e => e.VesselId, e => new CommNetEndpointSnapshot(e));
                commNetPreferences = (data.Preferences ?? Array.Empty<CommNetPreference>()).ToDictionary(p => p.Source.VesselId, p => new CommNetPreferenceSnapshot(p));
                commNetRevision = data.Revision;
                commNetReady = data.Ready;
            }
            Harmony.CommNet_AgencyFilter.RequestRefresh();
        }
        internal void InvalidateCommNet()
        {
            lock (commNetLock)
            {
                commNetReady = false;
                commNetEndpoints = new Dictionary<Guid, CommNetEndpointSnapshot>();
            }
            Harmony.CommNet_AgencyFilter.RequestRefresh();
        }
        internal void ApplyCommNetResult(AgencyCommNetResultMsgData result)
        {
            lock (commNetLock) latestCommNetResult = new CommNetResultSnapshot { RequestId = result.RequestId, Success = result.Success, Reason = result.Reason };
        }
        internal void ClearCommNet()
        {
            lock (commNetLock)
            {
                commNetReady = false; commNetRevision = -1; latestCommNetResult = null;
                commNetEndpoints = new Dictionary<Guid, CommNetEndpointSnapshot>(); commNetPreferences = new Dictionary<Guid, CommNetPreferenceSnapshot>();
                commNetPolicyPair[0] = null; commNetPolicyPair[1] = null;
            }
            Harmony.CommNet_AgencyFilter.RequestRefresh(true);
        }
    }
    public partial class AgencyMessageSender
    {
        public Guid SendCommNetCommand(CommNetOperation operation, Guid vesselId, Guid targetVesselId = default(Guid), bool enabled = false)
        {
            var data = global::LmpClient.Network.NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyCommNetCommandMsgData>();
            data.RequestId = Guid.NewGuid(); data.VesselId = vesselId; data.TargetVesselId = targetVesselId; data.Operation = operation; data.Enabled = enabled;
            var request = data.RequestId; SendMessage(data); return request;
        }
    }
}
