using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace KspControl.Bridge
{
    /// <summary>
    /// The standing live spend cap fixed for each (grant id, generation) at the grant's first lease acquire, so retries and restarts cannot move it.
    /// Keys are "grantId#generation". Implementations are called under the authority's gate.
    /// </summary>
    internal interface ISpendCapStore
    {
        /// <summary>Returns the stored caps. Throws <see cref="InvalidDataException"/> or <see cref="IOException"/> when unreadable.</summary>
        Dictionary<string, long> Load();
        void Save(IDictionary<string, long> caps);
    }

    internal sealed class MemorySpendCapStore : ISpendCapStore
    {
        private readonly object gate = new object();
        private Dictionary<string, long> items = new Dictionary<string, long>(StringComparer.Ordinal);
        public bool FailLoad { get; set; }
        public int Saves { get; private set; }
        public Dictionary<string, long> Load()
        {
            lock (gate)
            {
                if (FailLoad) throw new InvalidDataException("spend_caps_unreadable");
                return new Dictionary<string, long>(items, StringComparer.Ordinal);
            }
        }
        public void Save(IDictionary<string, long> caps) { lock (gate) { items = new Dictionary<string, long>(caps, StringComparer.Ordinal); Saves++; } }
    }

    /// <summary>JSON object at <c>&lt;KSP root&gt;/KspControlData/control/spendcaps.json</c>, written atomically.</summary>
    internal sealed class FileSpendCapStore : ISpendCapStore
    {
        private const int MaxBytes = 65536;
        private readonly string path;
        public FileSpendCapStore(string path) { this.path = path ?? throw new ArgumentNullException(nameof(path)); }

        public Dictionary<string, long> Load()
        {
            if (!File.Exists(path)) return new Dictionary<string, long>(StringComparer.Ordinal);
            if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("spend_caps_too_large");
            try
            {
                var text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
                var items = JsonConvert.DeserializeObject<Dictionary<string, long>>(text);
                if (items == null) throw new InvalidDataException("spend_caps_null");
                foreach (var item in items) if (item.Value < 0) throw new InvalidDataException("spend_caps_invalid");
                return new Dictionary<string, long>(items, StringComparer.Ordinal);
            }
            catch (JsonException) { throw new InvalidDataException("spend_caps_invalid"); }
            catch (DecoderFallbackException) { throw new InvalidDataException("spend_caps_invalid"); }
        }

        public void Save(IDictionary<string, long> caps)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(caps, Formatting.Indented));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch { try { File.Delete(temporary); } catch { } throw; }
        }
    }
}
