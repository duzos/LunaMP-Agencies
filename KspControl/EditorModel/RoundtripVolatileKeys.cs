using System;
using System.Collections.Generic;
namespace KspControl.EditorModel
{
    public enum VolatileRule
    {
        /// <summary>The key is excluded from comparison and fingerprints.</summary>
        Ignore,
        /// <summary>The value is a craft-space vector or quaternion compared relative to each craft's root part.</summary>
        RootRelativeVector,
        /// <summary>Each side's value must equal its own craft header key (for example description); sides are not compared to each other.</summary>
        HeaderDerived,
        /// <summary>
        /// A missing key counts as <see cref="VolatileKeyEntry.DefaultValue"/>. This rule type is an amendment to the plan's registry
        /// rules (ignore | rootRelativeVector | headerDerived), added on evidence 33-log 18:51: hand-built craft write
        /// "active = False" on the Autostrut actions while structurally loaded ones omit it.
        /// </summary>
        AbsentEqualsDefault
    }
    public sealed class VolatileKeyEntry
    {
        /// <summary>Module name, "PART", "HEADER", or a container node name (ACTIONS, EVENTS, PARTDATA, VESSELNAMING).</summary>
        public string Module { get; set; }
        /// <summary>Key path inside the module node; nested node names are joined with '/', e.g. "AutostrutOff/active".</summary>
        public string KeyPath { get; set; }
        public VolatileRule Rule { get; set; }
        public string HeaderKey { get; set; }
        public string DefaultValue { get; set; }
        public string EvidenceRef { get; set; }
        public string Describe() { return Module + "." + KeyPath + ":" + Rule; }
    }
    /// <summary>Evidence-backed registry of keys that legitimately differ between a roundtrip and its source. Every entry needs logged live evidence.</summary>
    public sealed class RoundtripVolatileKeys
    {
        private readonly List<VolatileKeyEntry> entries = new List<VolatileKeyEntry>();
        public IReadOnlyList<VolatileKeyEntry> Entries { get { return entries; } }
        public RoundtripVolatileKeys Add(VolatileKeyEntry entry) { entries.Add(entry); return this; }
        public VolatileKeyEntry Find(string module, string keyPath)
        {
            foreach (var e in entries)
                if (string.Equals(e.Module, module, StringComparison.Ordinal) && string.Equals(e.KeyPath, keyPath, StringComparison.Ordinal)) return e;
            return null;
        }
        public static RoundtripVolatileKeys Empty() { return new RoundtripVolatileKeys(); }
        /// <summary>The seeded registry.</summary>
        public static RoundtripVolatileKeys Default()
        {
            var r = new RoundtripVolatileKeys();
            r.Add(new VolatileKeyEntry { Module = "ModuleCryoTank", KeyPath = "LastUpdateTime", Rule = VolatileRule.Ignore, EvidenceRef = "33-log 18:25" });
            r.Add(new VolatileKeyEntry { Module = "RasterPropMonitorComputer", KeyPath = "vesselDescription", Rule = VolatileRule.HeaderDerived, HeaderKey = "description", EvidenceRef = "33-log 18:40" });
            foreach (var action in new[] { "AutostrutOff", "AutostrutRoot", "AutostrutHeaviest", "AutostrutGrandparent" })
                r.Add(new VolatileKeyEntry { Module = "ACTIONS", KeyPath = action + "/active", Rule = VolatileRule.AbsentEqualsDefault, DefaultValue = "False", EvidenceRef = "33-log 18:51" });
            return r;
        }
    }
}
