using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace KspControl.Bridge
{
    /// <summary>A grant generation the human stopped, or that tripped a fault. It survives a game restart.</summary>
    internal sealed class Suspension
    {
        [JsonProperty("grantId")] public string GrantId { get; set; }
        [JsonProperty("generation")] public long Generation { get; set; }
        /// <summary>"stop" or "fault".</summary>
        [JsonProperty("reason")] public string Reason { get; set; }
        [JsonProperty("utc")] public string Utc { get; set; }
    }

    /// <summary>Persisted suspensions. Implementations must be safe to call from any single thread at a time.</summary>
    internal interface ISuspensionStore
    {
        /// <summary>Returns the stored entries. Throws <see cref="InvalidDataException"/> or <see cref="IOException"/> when unreadable.</summary>
        List<Suspension> Load();
        void Save(IList<Suspension> items);
    }

    internal sealed class MemorySuspensionStore : ISuspensionStore
    {
        private readonly object gate = new object();
        private List<Suspension> items = new List<Suspension>();
        public bool FailLoad { get; set; }
        public bool FailSave { get; set; }
        public int Saves { get; private set; }
        public List<Suspension> Load()
        {
            lock (gate)
            {
                if (FailLoad) throw new InvalidDataException("suspensions_unreadable");
                return items.ConvertAll(s => new Suspension { GrantId = s.GrantId, Generation = s.Generation, Reason = s.Reason, Utc = s.Utc });
            }
        }
        public void Save(IList<Suspension> value)
        {
            lock (gate)
            {
                if (FailSave) throw new IOException("suspensions_unwritable");
                items = new List<Suspension>(value); Saves++;
            }
        }
    }

    /// <summary>JSON array at <c>&lt;KSP root&gt;/KspControlData/control/suspensions.json</c>, written atomically.</summary>
    internal sealed class FileSuspensionStore : ISuspensionStore
    {
        private const int MaxBytes = 262144;
        private readonly string path;
        public FileSuspensionStore(string path)
        { this.path = path ?? throw new ArgumentNullException(nameof(path)); }
        public List<Suspension> Load()
        {
            if (!File.Exists(path)) return new List<Suspension>();
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) throw new InvalidDataException("suspensions_too_large");
            try
            {
                var text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
                var items = JsonConvert.DeserializeObject<List<Suspension>>(text);
                if (items == null) throw new InvalidDataException("suspensions_null");
                foreach (var item in items)
                    if (item == null || string.IsNullOrEmpty(item.GrantId) || item.Generation <= 0) throw new InvalidDataException("suspensions_invalid");
                return items;
            }
            catch (JsonException) { throw new InvalidDataException("suspensions_invalid"); }
            catch (DecoderFallbackException) { throw new InvalidDataException("suspensions_invalid"); }
        }
        public void Save(IList<Suspension> items)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(items, Formatting.Indented));
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
