using System.IO;
using System.Text;
using Lidgren.Network;
using Newtonsoft.Json;
namespace LmpCommon.Agency
{
    public static class AgencyEconomyWire
    {
        public const int MaximumPayloadBytes = 4 * 1024 * 1024;
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            MaxDepth = 24,
            TypeNameHandling = TypeNameHandling.None,
            FloatParseHandling = FloatParseHandling.Double
        };

        public static int Size<T>(T value) => sizeof(int) + Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(value, Settings));

        public static void Write<T>(NetOutgoingMessage message, T value)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Settings));
            if (bytes.Length > MaximumPayloadBytes) throw new InvalidDataException("Economy message exceeds limit.");
            message.Write(bytes.Length);
            message.Write(bytes);
        }

        public static T Read<T>(NetIncomingMessage message) where T : class
        {
            VesselOwnershipWire.Require(message, 32);
            var count = message.ReadInt32();
            if (count < 1 || count > MaximumPayloadBytes) throw new InvalidDataException("Invalid economy payload length.");
            VesselOwnershipWire.Require(message, checked(count * 8));
            var json = new UTF8Encoding(false, true).GetString(message.ReadBytes(count));
            return JsonConvert.DeserializeObject<T>(json, Settings) ?? throw new InvalidDataException("Missing economy payload.");
        }
    }
}
