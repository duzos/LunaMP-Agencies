using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Lidgren.Network;

namespace LmpCommon.Agency
{
    public sealed class FirstAchievement
    {
        public string Key = string.Empty;
        public long UtcTicks;
        public AchievementDetails Details;
    }

    public sealed class AchievementDetails
    {
        public double? UniversalTime;
        public Guid VesselId;
        public string VesselName = string.Empty;
        // null means unknown; an empty array means known uncrewed.
        public string[] CrewNames;
        public bool CrewTruncated;
    }

    public static class AchievementWire
    {
        public const int MaxName = 256, MaxCrew = 16;
        public static int StringSize(string value) => 5 + Encoding.UTF8.GetByteCount(value ?? string.Empty);
        private static void ValidateText(string value)
        {
            if ((value ?? string.Empty).Length > MaxName) throw new InvalidDataException("Achievement text too long.");
        }
        public static void Validate(AchievementDetails value)
        {
            if (value == null) return;
            if (value.UniversalTime.HasValue && (double.IsNaN(value.UniversalTime.Value) || double.IsInfinity(value.UniversalTime.Value) || value.UniversalTime.Value < 0)) throw new InvalidDataException("Invalid achievement time.");
            ValidateText(value.VesselName);
            if (value.CrewTruncated && (value.CrewNames == null || value.CrewNames.Length == 0)) throw new InvalidDataException("Truncated crew requires a recorded crew list.");
            if (value.CrewNames == null) return;
            if (value.CrewNames.Length > MaxCrew) throw new InvalidDataException("Too many crew names.");
            foreach (var name in value.CrewNames) ValidateText(name);
        }
        public static AchievementDetails Copy(AchievementDetails value)
        {
            Validate(value);
            return value == null ? null : new AchievementDetails { UniversalTime = value.UniversalTime, VesselId = value.VesselId, VesselName = value.VesselName, CrewNames = value.CrewNames?.ToArray(), CrewTruncated = value.CrewTruncated };
        }
        public static string ReadText(NetIncomingMessage msg)
        {
            var bytes = msg.ReadVariableUInt32();
            if (bytes > MaxName * 4 || bytes * 8L > msg.LengthBits - msg.Position) throw new InvalidDataException("Invalid achievement text length.");
            var value = Encoding.UTF8.GetString(msg.ReadBytes((int)bytes));
            ValidateText(value);
            return value;
        }
        public static void WriteDetails(NetOutgoingMessage msg, AchievementDetails value)
        {
            Validate(value);
            msg.Write(value != null);
            if (value == null) return;
            msg.Write(value.UniversalTime.HasValue);
            if (value.UniversalTime.HasValue) msg.Write(value.UniversalTime.Value);
            msg.Write(value.VesselId.ToByteArray()); msg.Write(value.VesselName ?? string.Empty);
            msg.Write(value.CrewNames?.Length ?? -1);
            if (value.CrewNames != null) foreach (var name in value.CrewNames) msg.Write(name ?? string.Empty);
            msg.Write(value.CrewTruncated);
        }
        public static AchievementDetails ReadDetails(NetIncomingMessage msg)
        {
            if (!msg.ReadBoolean()) return null;
            var value = new AchievementDetails();
            if (msg.ReadBoolean()) value.UniversalTime = msg.ReadDouble();
            value.VesselId = new Guid(msg.ReadBytes(16)); value.VesselName = ReadText(msg);
            var count = msg.ReadInt32();
            if (count < -1 || count > MaxCrew) throw new InvalidDataException("Invalid achievement crew count.");
            if (count >= 0) { value.CrewNames = new string[count]; for (var i = 0; i < count; i++) value.CrewNames[i] = ReadText(msg); }
            value.CrewTruncated = msg.ReadBoolean();
            Validate(value); return value;
        }
        public static int Size(AchievementDetails value) => value == null ? 1 : 31 + StringSize(value.VesselName) + (value.CrewNames?.Sum(StringSize) ?? 0);
        public static string ReadableName(string key) => Regex.Replace((key ?? "Unknown milestone").Replace(":", " / ").Replace("_", " "), "([a-z])([A-Z])", "$1 $2");
    }
}
