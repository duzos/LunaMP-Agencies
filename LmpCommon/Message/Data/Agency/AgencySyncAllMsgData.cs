using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LmpCommon.Message.Data.Agency
{
    public class AgencySyncAllMsgData : AgencyBaseMsgData
    {
        internal AgencySyncAllMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvSyncAll;
        public override string ClassName { get; } = nameof(AgencySyncAllMsgData);

        /// <summary>
        /// The recipient's current agency id (Guid.Empty if none).
        /// Allows clients to know "this is me" without a second roundtrip.
        /// </summary>
        public Guid MyAgencyId;

        public AgencyInfo[] Agencies = Array.Empty<AgencyInfo>();

        public bool LaunchSitesSnapshotPresent;
        public long LaunchSitesRevision;
        public LaunchSiteAssignment[] LaunchSites = Array.Empty<LaunchSiteAssignment>();

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(MyAgencyId.ToByteArray());
            var arr = Agencies ?? Array.Empty<AgencyInfo>();
            lidgrenMsg.Write(arr.Length);
            foreach (var a in arr)
                AgencyWireHelpers.WriteAgencyInfo(lidgrenMsg, a);
            lidgrenMsg.Write(LaunchSitesSnapshotPresent);
            if (LaunchSitesSnapshotPresent)
            {
                var assignments = LaunchSites ?? Array.Empty<LaunchSiteAssignment>();
                if (assignments.Length > AgencyLaunchSitePolicy.MaxAssignments || LaunchSitesRevision < 0)
                    throw new InvalidDataException("Invalid launch-site snapshot bounds.");
                lidgrenMsg.Write(LaunchSitesRevision);
                lidgrenMsg.Write(assignments.Length);
                foreach (var assignment in assignments)
                {
                    if (assignment == null || !AgencyLaunchSitePolicy.IsValidSiteId(assignment.SiteId) || assignment.AgencyId == Guid.Empty)
                        throw new InvalidDataException("Invalid launch-site assignment.");
                    lidgrenMsg.Write(assignment.SiteId);
                    lidgrenMsg.Write(assignment.AgencyId.ToByteArray());
                }
            }
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            LaunchSitesSnapshotPresent = false;
            LaunchSitesRevision = 0;
            LaunchSites = Array.Empty<LaunchSiteAssignment>();
            MyAgencyId = new Guid(lidgrenMsg.ReadBytes(16));
            var count = lidgrenMsg.ReadInt32();
            if (count < 0) count = 0;
            Agencies = new AgencyInfo[count];
            for (int i = 0; i < count; i++)
                Agencies[i] = AgencyWireHelpers.ReadAgencyInfo(lidgrenMsg);
            // Optional tail: older peers stop after the last agency. Never publish a partial snapshot.
            try
            {
                if (lidgrenMsg.Position >= lidgrenMsg.LengthBits || !lidgrenMsg.ReadBoolean()) return;
                if (lidgrenMsg.LengthBits - lidgrenMsg.Position < 96) return;
                var revision = lidgrenMsg.ReadInt64();
                var assignmentCount = lidgrenMsg.ReadInt32();
                if (revision < 0 || assignmentCount < 0 || assignmentCount > AgencyLaunchSitePolicy.MaxAssignments) return;
                var assignments = new LaunchSiteAssignment[assignmentCount];
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < assignmentCount; i++)
                {
                    var bytes = lidgrenMsg.ReadVariableUInt32();
                    if (bytes > AgencyLaunchSitePolicy.MaxSiteIdLength * 4 || bytes * 8L + 128 > lidgrenMsg.LengthBits - lidgrenMsg.Position) return;
                    var site = Encoding.UTF8.GetString(lidgrenMsg.ReadBytes((int)bytes));
                    var agency = new Guid(lidgrenMsg.ReadBytes(16));
                    if (!AgencyLaunchSitePolicy.IsValidSiteId(site) || agency == Guid.Empty || !seen.Add(site)) return;
                    assignments[i] = new LaunchSiteAssignment { SiteId = site, AgencyId = agency };
                }
                LaunchSitesRevision = revision;
                LaunchSites = assignments;
                LaunchSitesSnapshotPresent = true;
            }
            catch (NetException) { /* Truncated optional tail is absent, not a partial permission set. */ }
            catch (ArgumentException) { /* Malformed optional data cannot retain pooled permission state. */ }
        }

        internal override int InternalGetMessageSize()
        {
            var size = 16 + sizeof(int) + 1;
            foreach (var agency in Agencies ?? Array.Empty<AgencyInfo>())
            {
                size += 69 + StringSize(agency.Name) + StringSize(agency.OwnerUniqueId) + StringSize(agency.OwnerDisplayName);
                var ids = agency.MemberUniqueIds ?? Array.Empty<string>();
                var names = agency.MemberDisplayNames ?? Array.Empty<string>();
                for (var i = 0; i < ids.Length; i++) size += StringSize(ids[i]) + StringSize(i < names.Length ? names[i] : null);
            }
            if (LaunchSitesSnapshotPresent)
            {
                size += sizeof(long) + sizeof(int);
                foreach (var assignment in LaunchSites ?? Array.Empty<LaunchSiteAssignment>()) size += 16 + StringSize(assignment?.SiteId);
            }
            return size;
        }

        private static int StringSize(string value) => 5 + Encoding.UTF8.GetByteCount(value ?? string.Empty);
    }
}
