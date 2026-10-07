using System;
using System.IO;
using System.Linq;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Agency
{
    public class AgencyIdentitySnapshotMsgData : AgencyBaseMsgData
    {
        internal AgencyIdentitySnapshotMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvIdentitySnapshot;
        public override string ClassName => nameof(AgencyIdentitySnapshotMsgData);
        public AgencyIdentityInfo[] Identities = Array.Empty<AgencyIdentityInfo>();
        internal override void InternalSerialize(NetOutgoingMessage m)
        {
            if (Identities == null || Identities.Length > AgencyIdentityDefaults.MaxIdentities) throw new InvalidDataException();
            m.Write(Identities.Length); foreach (var identity in Identities) AgencyIdentityWire.Write(m, identity);
        }
        internal override void InternalDeserialize(NetIncomingMessage m)
        {
            Identities = Array.Empty<AgencyIdentityInfo>(); VesselOwnershipWire.Require(m, 32); var count = m.ReadInt32();
            if (count < 0 || count > AgencyIdentityDefaults.MaxIdentities) throw new InvalidDataException();
            VesselOwnershipWire.Require(m, count * 249);
            var entries = new AgencyIdentityInfo[count];
            for (int i = 0; i < count; i++) entries[i] = AgencyIdentityWire.Read(m);
            if (entries.Select(x => x.AgencyId).Distinct().Count() != count) throw new InvalidDataException();
            Identities = entries;
        }
        internal override int InternalGetMessageSize() => 4 + Identities.Sum(AgencyIdentityWire.Size);
    }
    public class AgencyIdentityUpsertMsgData : AgencyBaseMsgData
    {
        internal AgencyIdentityUpsertMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvIdentityUpsert;
        public override string ClassName => nameof(AgencyIdentityUpsertMsgData);
        public AgencyIdentityInfo Identity;
        internal override void InternalSerialize(NetOutgoingMessage m) => AgencyIdentityWire.Write(m, Identity);
        internal override void InternalDeserialize(NetIncomingMessage m) { Identity = null; Identity = AgencyIdentityWire.Read(m); }
        internal override int InternalGetMessageSize() => AgencyIdentityWire.Size(Identity);
    }
    public class AgencySetIdentityMsgData : AgencyBaseMsgData
    {
        internal AgencySetIdentityMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliSetIdentity;
        public override string ClassName => nameof(AgencySetIdentityMsgData);
        public Guid AgencyId;
        public long ExpectedRevision;
        public bool HasColour;
        public byte Red, Green, Blue;
        public string FlagUrl = AgencyIdentityDefaults.DefaultFlagUrl, FlagSha256 = string.Empty;
        internal override void InternalSerialize(NetOutgoingMessage m)
        {
            AgencyIdentityWire.Write(m, new AgencyIdentityInfo { AgencyId = AgencyId, Revision = ExpectedRevision, HasColour = HasColour, Red = Red, Green = Green, Blue = Blue, FlagUrl = FlagUrl });
            AgencyIdentityWire.WriteText(m, FlagSha256, 64);
        }
        internal override void InternalDeserialize(NetIncomingMessage m)
        {
            AgencyId = Guid.Empty; ExpectedRevision = 0; HasColour = false; Red = Green = Blue = 0; FlagUrl = FlagSha256 = string.Empty;
            var identity = AgencyIdentityWire.Read(m); var hash = AgencyIdentityWire.ReadText(m, 64);
            AgencyId = identity.AgencyId; ExpectedRevision = identity.Revision; HasColour = identity.HasColour;
            Red = identity.Red; Green = identity.Green; Blue = identity.Blue; FlagUrl = identity.FlagUrl; FlagSha256 = hash;
        }
        internal override int InternalGetMessageSize() => 36 + (FlagUrl?.Length ?? 0) + (FlagSha256?.Length ?? 0);
    }
}
