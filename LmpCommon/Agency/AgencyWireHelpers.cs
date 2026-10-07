using Lidgren.Network;
using System;
using System.IO;
using System.Linq;

namespace LmpCommon.Agency
{
    /// <summary>
    /// Serialization helpers for AgencyInfo and JoinRequestInfo over the wire.
    /// Kept in one place so every agency message stays consistent when fields
    /// are added.
    /// </summary>
    public static class AgencyWireHelpers
    {
        public static void WriteAgencyInfo(NetOutgoingMessage msg, AgencyInfo a)
        {
            msg.Write(a.Id.ToByteArray());
            msg.Write(a.Name ?? string.Empty);
            msg.Write(a.OwnerUniqueId ?? string.Empty);
            msg.Write(a.OwnerDisplayName ?? string.Empty);

            var memberIds = a.MemberUniqueIds ?? Array.Empty<string>();
            var memberNames = a.MemberDisplayNames ?? Array.Empty<string>();
            msg.Write(memberIds.Length);
            for (int i = 0; i < memberIds.Length; i++)
            {
                msg.Write(memberIds[i] ?? string.Empty);
                var name = i < memberNames.Length ? memberNames[i] : string.Empty;
                msg.Write(name ?? string.Empty);
            }

            msg.Write(a.Funds);
            msg.Write(a.Science);
            msg.Write(a.Reputation);
            msg.Write(a.CreatedUtcTicks);
            msg.Write(a.IsSolo);
            msg.Write(a.UnlockedTechCount);
            msg.Write(a.LifetimeFundsEarned);
            msg.Write(a.LifetimeScienceGenerated);
            msg.Write(a.VesselsLaunched);
            var firsts = a.FirstAchievements ?? Array.Empty<FirstAchievement>();
            msg.Write(firsts.Length);
            foreach (var first in firsts)
            {
                if (first == null || first.Key == null || first.Key.Length > AchievementWire.MaxName) throw new InvalidDataException("Invalid first key.");
                msg.Write(first.Key); msg.Write(first.UtcTicks); AchievementWire.WriteDetails(msg, first.Details);
            }
        }

        public static AgencyInfo ReadAgencyInfo(NetIncomingMessage msg)
        {
            var a = new AgencyInfo();
            a.Id = new Guid(msg.ReadBytes(16));
            a.Name = msg.ReadString();
            a.OwnerUniqueId = msg.ReadString();
            a.OwnerDisplayName = msg.ReadString();

            var count = msg.ReadInt32();
            if (count < 0) count = 0;
            a.MemberUniqueIds = new string[count];
            a.MemberDisplayNames = new string[count];
            for (int i = 0; i < count; i++)
            {
                a.MemberUniqueIds[i] = msg.ReadString();
                a.MemberDisplayNames[i] = msg.ReadString();
            }

            a.Funds = msg.ReadDouble();
            a.Science = msg.ReadFloat();
            a.Reputation = msg.ReadFloat();
            a.CreatedUtcTicks = msg.ReadInt64();
            a.IsSolo = msg.ReadBoolean();
            a.UnlockedTechCount = msg.ReadInt32();
            a.LifetimeFundsEarned = msg.ReadDouble();
            a.LifetimeScienceGenerated = msg.ReadFloat();
            a.VesselsLaunched = msg.ReadInt32();
            var firstCount = msg.ReadInt32();
            if (firstCount < 0 || firstCount > (msg.LengthBits - msg.Position) / 73) throw new InvalidDataException("Invalid first count.");
            a.FirstAchievements = new FirstAchievement[firstCount];
            for (var i = 0; i < firstCount; i++) a.FirstAchievements[i] = new FirstAchievement { Key = AchievementWire.ReadText(msg), UtcTicks = msg.ReadInt64(), Details = AchievementWire.ReadDetails(msg) };
            return a;
        }

        public static int AgencyInfoSize(AgencyInfo a)
        {
            var size = 73 + AchievementWire.StringSize(a.Name) + AchievementWire.StringSize(a.OwnerUniqueId) + AchievementWire.StringSize(a.OwnerDisplayName);
            var ids = a.MemberUniqueIds ?? Array.Empty<string>(); var names = a.MemberDisplayNames ?? Array.Empty<string>();
            for (var i = 0; i < ids.Length; i++) size += AchievementWire.StringSize(ids[i]) + AchievementWire.StringSize(i < names.Length ? names[i] : null);
            foreach (var first in a.FirstAchievements ?? Array.Empty<FirstAchievement>()) size += AchievementWire.StringSize(first.Key) + 8 + AchievementWire.Size(first.Details);
            return size;
        }

        public static void WriteJoinRequestInfo(NetOutgoingMessage msg, JoinRequestInfo r)
        {
            msg.Write(r.AgencyId.ToByteArray());
            msg.Write(r.PlayerUniqueId ?? string.Empty);
            msg.Write(r.PlayerDisplayName ?? string.Empty);
            msg.Write(r.RequestedUtcTicks);
        }

        public static JoinRequestInfo ReadJoinRequestInfo(NetIncomingMessage msg)
        {
            var r = new JoinRequestInfo();
            r.AgencyId = new Guid(msg.ReadBytes(16));
            r.PlayerUniqueId = msg.ReadString();
            r.PlayerDisplayName = msg.ReadString();
            r.RequestedUtcTicks = msg.ReadInt64();
            return r;
        }
    }
}
