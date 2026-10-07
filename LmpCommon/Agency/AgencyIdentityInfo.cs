using System;
using System.IO;
using System.Linq;
using System.Text;
using Lidgren.Network;
using LmpCommon.Flags;

namespace LmpCommon.Agency
{
    public sealed class AgencyIdentityInfo
    {
        public Guid AgencyId;
        public long Revision;
        public bool HasColour;
        public byte Red, Green, Blue;
        public string FlagUrl = AgencyIdentityDefaults.DefaultFlagUrl;
    }

    public static class AgencyIdentityDefaults
    {
        public const int ProtocolVersion = 1, MaxFlagUrlLength = 256, MaxIdentities = 4096;
        public const string DefaultFlagUrl = "Squad/Flags/default";
        public static bool IsStockFlag(string url) => DefaultFlags.DefaultFlagList.Contains(url);
        public static bool IsSafeFlagUrl(string url) => !string.IsNullOrEmpty(url) && url.Length <= MaxFlagUrlLength &&
            url.Split('/').All(p => p.Length > 0 && p.All(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-'));
    }

    public static class AgencyIdentityWire
    {
        public static void Validate(AgencyIdentityInfo identity)
        {
            if (identity == null || identity.AgencyId == Guid.Empty || identity.Revision < 0 || !AgencyIdentityDefaults.IsSafeFlagUrl(identity.FlagUrl))
                throw new InvalidDataException("Invalid agency identity.");
        }
        public static void WriteText(NetOutgoingMessage m, string text, int max)
        {
            if (text == null || text.Length > max || text.Any(c => c > 127)) throw new InvalidDataException("Invalid identity text.");
            var bytes = Encoding.ASCII.GetBytes(text); m.Write(bytes.Length); m.Write(bytes);
        }
        public static string ReadText(NetIncomingMessage m, int max)
        {
            VesselOwnershipWire.Require(m, 32); var length = m.ReadInt32();
            if (length < 0 || length > max) throw new InvalidDataException("Invalid identity text length.");
            VesselOwnershipWire.Require(m, length * 8);
            var bytes = m.ReadBytes(length);
            if (bytes.Any(b => b > 127)) throw new InvalidDataException("Invalid identity text.");
            return Encoding.ASCII.GetString(bytes);
        }
        public static void Write(NetOutgoingMessage m, AgencyIdentityInfo identity)
        {
            Validate(identity); m.Write(identity.AgencyId.ToByteArray()); m.Write(identity.Revision);
            m.Write(identity.HasColour); m.Write(identity.Red); m.Write(identity.Green); m.Write(identity.Blue);
            WriteText(m, identity.FlagUrl, AgencyIdentityDefaults.MaxFlagUrlLength);
        }
        public static AgencyIdentityInfo Read(NetIncomingMessage m)
        {
            VesselOwnershipWire.Require(m, 217);
            var identity = new AgencyIdentityInfo { AgencyId = new Guid(m.ReadBytes(16)), Revision = m.ReadInt64(), HasColour = m.ReadBoolean(), Red = m.ReadByte(), Green = m.ReadByte(), Blue = m.ReadByte(), FlagUrl = ReadText(m, AgencyIdentityDefaults.MaxFlagUrlLength) };
            Validate(identity); return identity;
        }
        public static int Size(AgencyIdentityInfo identity) => 32 + (identity?.FlagUrl?.Length ?? 0);
    }
}
