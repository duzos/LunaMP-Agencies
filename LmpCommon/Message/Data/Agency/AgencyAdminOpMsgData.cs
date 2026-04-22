using Lidgren.Network;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Single admin action carrier. AdminPassword authenticates the caller
    /// (mirrors the AdminMsgData pattern). Op selects behavior; TargetAgencyId,
    /// StringArg, and NumericArg are interpreted per-op — see AgencyAdminOp
    /// and AgencyAdminCliReader on the server.
    /// </summary>
    public class AgencyAdminOpMsgData : AgencyBaseMsgData
    {
        internal AgencyAdminOpMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliAdminOp;
        public override string ClassName { get; } = nameof(AgencyAdminOpMsgData);

        public string AdminPassword = string.Empty;
        public AgencyAdminOp Op;
        public Guid TargetAgencyId;
        public string StringArg = string.Empty;
        public double NumericArg;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(AdminPassword ?? string.Empty);
            lidgrenMsg.Write((byte)Op);
            lidgrenMsg.Write(TargetAgencyId.ToByteArray());
            lidgrenMsg.Write(StringArg ?? string.Empty);
            lidgrenMsg.Write(NumericArg);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            AdminPassword = lidgrenMsg.ReadString();
            Op = (AgencyAdminOp)lidgrenMsg.ReadByte();
            TargetAgencyId = new Guid(lidgrenMsg.ReadBytes(16));
            StringArg = lidgrenMsg.ReadString();
            NumericArg = lidgrenMsg.ReadDouble();
        }

        internal override int InternalGetMessageSize() => 64 + sizeof(byte) + 16 + 64 + sizeof(double);
    }
}
