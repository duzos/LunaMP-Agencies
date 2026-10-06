using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using LmpCommon.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;

namespace LmpClient.Systems.Agency
{
    public sealed class VesselOwnershipRecordSnapshot
    {
        public Guid VesselId { get; }
        public Guid OwnerAgencyId { get; }
        public IReadOnlyList<Guid> CoOwnerAgencyIds { get; }
        public VesselDockingPolicy DockingPolicy { get; }
        public long Revision { get; }
        internal readonly VesselOwnershipRecord PolicyRecord;
        internal VesselOwnershipRecordSnapshot(VesselOwnershipRecord record)
        {
            VesselId = record.VesselId; OwnerAgencyId = record.OwnerAgencyId;
            CoOwnerAgencyIds = Array.AsReadOnly((record.CoOwnerAgencyIds ?? new Guid[0]).ToArray());
            DockingPolicy = record.DockingPolicy; Revision = record.Revision;
            PolicyRecord = record.Copy();
        }
    }
    public sealed class DockConsentSnapshot
    {
        public Guid RequestId { get; internal set; }
        public Guid SourceVesselId { get; internal set; }
        public Guid TargetVesselId { get; internal set; }
        public Guid RequesterAgencyId { get; internal set; }
        public string RequesterName { get; internal set; }
        public DockConsentStatus Status { get; internal set; }
        public long ExpiresUtcTicks { get; internal set; }
        public string Reason { get; internal set; }
        public Guid GrantId { get; internal set; }
        public Guid OperationId { get; internal set; }
    }
    public sealed class OwnershipResultSnapshot
    {
        public Guid RequestId { get; internal set; }
        public Guid VesselId { get; internal set; }
        public bool Success { get; internal set; }
        public string Reason { get; internal set; }
    }
    public partial class AgencySystem
    {
        private readonly object ownershipLock = new object();
        private readonly Dictionary<Guid, VesselOwnershipRecordSnapshot> ownership = new Dictionary<Guid, VesselOwnershipRecordSnapshot>();
        private readonly Dictionary<Guid, DockConsentSnapshot> dockRequests = new Dictionary<Guid, DockConsentSnapshot>();
        private bool ownershipReady;
        private long ownershipRevision = -1;
        private OwnershipResultSnapshot latestOwnershipResult;
        internal readonly global::System.Collections.Concurrent.ConcurrentQueue<DockConsentSnapshot> DockNotifications = new global::System.Collections.Concurrent.ConcurrentQueue<DockConsentSnapshot>();
        internal volatile bool ResetOwnershipUi;
        public static bool OwnershipEnabled => MainSystem.NetworkState >= ClientState.Handshaking && SettingsSystem.ServerSettings.AgencyVesselOwnership;
        public bool OwnershipReady { get { lock (ownershipLock) return ownershipReady; } }
        public OwnershipResultSnapshot LatestOwnershipResult { get { lock (ownershipLock) return latestOwnershipResult; } }
        public IReadOnlyDictionary<Guid, VesselOwnershipRecordSnapshot> GetOwnershipSnapshot()
        {
            lock (ownershipLock) return new ReadOnlyDictionary<Guid, VesselOwnershipRecordSnapshot>(new Dictionary<Guid, VesselOwnershipRecordSnapshot>(ownership));
        }
        public IReadOnlyList<DockConsentSnapshot> GetDockRequests()
        {
            lock (ownershipLock) return Array.AsReadOnly(dockRequests.Values.OrderByDescending(r => r.ExpiresUtcTicks).ToArray());
        }
        public IReadOnlyList<DockConsentSnapshot> PendingDockRequests => GetDockRequests().Where(r => r.Status == DockConsentStatus.Pending).ToArray();
        public bool CanControlVessel(Guid vesselId)
        {
            if (!OwnershipEnabled) return true;
            var agency = MyAgencyId;
            lock (ownershipLock)
            {
                if (!ownershipReady || agency == Guid.Empty || vesselId == Guid.Empty) return false;
                ownership.TryGetValue(vesselId, out var record);
                return VesselOwnershipPolicy.CanControl(record?.PolicyRecord, agency);
            }
        }
        public bool CanManageVessel(Guid vesselId)
        {
            var agency = MyAgencyId;
            if (!OwnershipEnabled || !AmIOwnerOfMine()) return false;
            lock (ownershipLock)
            {
                ownership.TryGetValue(vesselId, out var record);
                return ownershipReady && VesselOwnershipPolicy.CanManage(record?.PolicyRecord, agency, true);
            }
        }
        internal void ApplyOwnership(bool present, long revision, IEnumerable<VesselOwnershipRecord> records, bool full)
        {
            lock (ownershipLock)
            {
                if (!present || records == null) { ownershipReady = false; ownership.Clear(); ownershipRevision = -1; return; }
                if (revision < ownershipRevision) return;
                var copy = records.ToArray();
                if (revision < 0 || copy.Length > VesselOwnershipPolicy.MaxRecords || copy.Any(r => r == null || r.VesselId == Guid.Empty || r.Revision < 0 ||
                    !Enum.IsDefined(typeof(VesselDockingPolicy), r.DockingPolicy) || r.CoOwnerAgencyIds == null || r.CoOwnerAgencyIds.Length > VesselOwnershipPolicy.MaxCoOwners ||
                    r.CoOwnerAgencyIds.Any(id => id == Guid.Empty) || r.CoOwnerAgencyIds.Distinct().Count() != r.CoOwnerAgencyIds.Length) ||
                    copy.Select(r => r.VesselId).Distinct().Count() != copy.Length)
                { ownershipReady = false; ownership.Clear(); ownershipRevision = -1; return; }
                if (full) ownership.Clear();
                foreach (var record in copy) ownership[record.VesselId] = new VesselOwnershipRecordSnapshot(record);
                ownershipRevision = revision;
                if (full) ownershipReady = true;
            }
        }
        internal void ApplyDockStatus(DockConsentSnapshot status)
        {
            lock (ownershipLock)
            {
                if (dockRequests.Count >= 128 && !dockRequests.ContainsKey(status.RequestId)) dockRequests.Remove(dockRequests.Values.OrderBy(r => r.ExpiresUtcTicks).First().RequestId);
                dockRequests[status.RequestId] = status;
            }
            DockNotifications.Enqueue(status);
        }
        internal void ApplyOwnershipEntry(LmpCommon.Message.Data.Agency.AgencyVesselMapEntryMsgData data)
        {
            if (data.OwnershipRecord != null && data.OwnershipRecord.VesselId != data.VesselId) { ApplyOwnership(false, 0, null, false); return; }
            if (data.OwnershipRecord != null) { ApplyOwnership(data.OwnershipSnapshotPresent, data.OwnershipRevision, new[] { data.OwnershipRecord }, false); return; }
            lock (ownershipLock)
            {
                if (!data.OwnershipSnapshotPresent) { ownershipReady = false; ownership.Clear(); ownershipRevision = -1; return; }
                if (data.OwnershipRevision < ownershipRevision) return;
                ownership.Remove(data.VesselId); ownershipRevision = data.OwnershipRevision;
            }
        }
        internal void ApplyOwnershipResult(OwnershipResultSnapshot result) { lock (ownershipLock) latestOwnershipResult = result; }
        internal void ClearOwnership()
        {
            lock (ownershipLock) { ownership.Clear(); dockRequests.Clear(); ownershipReady = false; ownershipRevision = -1; latestOwnershipResult = null; }
            while (DockNotifications.TryDequeue(out _)) { }
            ResetOwnershipUi = true;
            VesselPublicationGuard.ResetSession();
        }
    }
}
