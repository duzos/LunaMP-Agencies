using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
namespace KspControl.EditorModel
{
    /// <summary>A "name_cid" reference found in link, sym, srfN or attN.</summary>
    public sealed class CraftRef
    {
        public string Name;
        public string CidText;
        public uint Cid;
        public bool CidValid;
        public string VectorText;
        public string Key { get { return CidText; } }
    }
    public sealed class CraftAttN
    {
        public string NodeId;
        public CraftRef Partner;   // null when the node is open (Null_0)
        public string Raw;
    }
    /// <summary>Read-only view of one PART node, tolerant of malformed input (problems stay visible to the validator).</summary>
    public sealed class CraftPartInfo
    {
        public int Index;
        public ConfigNode Node;
        public string PartValue;
        public string Name;
        public string CidText;
        public uint Cid;
        public bool CidValid;
        public List<CraftRef> Links = new List<CraftRef>();
        public List<CraftRef> Sym = new List<CraftRef>();
        public List<CraftAttN> AttN = new List<CraftAttN>();
        public CraftRef Srf;
        public bool HasSrfValue;
        public int Parent = -1;
        public List<int> Children = new List<int>();
        public string Ref { get { return PartValue ?? ("PART#" + Index); } }
    }
    public sealed class CraftModel
    {
        public ConfigNode Root;
        public List<CraftPartInfo> Parts = new List<CraftPartInfo>();
        public Dictionary<string, int> ByCid = new Dictionary<string, int>(StringComparer.Ordinal);
        public List<string> DuplicateCids = new List<string>();
        private static readonly Regex RefRegex = new Regex(@"^(?<name>.+?)_(?<cid>\d+)(?:_(?<vec>.*))?$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
        public static CraftRef ParseRef(string text)
        {
            if (text == null) return null;
            var m = RefRegex.Match(text.Trim());
            if (!m.Success) return new CraftRef { Name = text.Trim(), CidText = "", CidValid = false };
            uint cid; var ok = uint.TryParse(m.Groups["cid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out cid);
            return new CraftRef { Name = m.Groups["name"].Value, CidText = ok ? cid.ToString(CultureInfo.InvariantCulture) : m.Groups["cid"].Value, Cid = cid, CidValid = ok, VectorText = m.Groups["vec"].Success ? m.Groups["vec"].Value : null };
        }
        /// <summary>Splits "name_cid" at the last underscore. Name may be empty when malformed.</summary>
        public static void SplitPart(string partValue, out string name, out string cidText, out uint cid, out bool valid)
        {
            name = partValue ?? ""; cidText = ""; cid = 0; valid = false;
            if (partValue == null) return;
            int i = partValue.LastIndexOf('_');
            if (i <= 0 || i == partValue.Length - 1) return;
            var tail = partValue.Substring(i + 1);
            if (!uint.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out cid)) return;
            name = partValue.Substring(0, i); cidText = cid.ToString(CultureInfo.InvariantCulture); valid = true;
        }
        public static CraftModel Build(ConfigNode root)
        {
            var model = new CraftModel { Root = root };
            foreach (var node in root.Children("PART"))
            {
                var info = new CraftPartInfo { Index = model.Parts.Count, Node = node, PartValue = node.First("part") };
                SplitPart(info.PartValue, out info.Name, out info.CidText, out info.Cid, out info.CidValid);
                foreach (var v in node.Values("link")) info.Links.Add(ParseRef(v));
                foreach (var v in node.Values("sym")) info.Sym.Add(ParseRef(v));
                foreach (var v in node.Values("attN"))
                {
                    int comma = v.IndexOf(',');
                    var a = new CraftAttN { Raw = v };
                    if (comma < 0) { a.NodeId = v.Trim(); }
                    else
                    {
                        a.NodeId = v.Substring(0, comma).Trim();
                        var r = ParseRef(v.Substring(comma + 1));
                        if (r != null && !(r.Name == "Null" && r.CidText == "0")) a.Partner = r;
                    }
                    info.AttN.Add(a);
                }
                var srf = node.First("srfN");
                if (srf != null)
                {
                    info.HasSrfValue = true;
                    int comma = srf.IndexOf(',');
                    if (comma >= 0)
                    {
                        var rest = srf.Substring(comma + 1);
                        int c2 = rest.IndexOf(',');
                        // Saved form is "srfAttach,<name>_<cid>,,<vectors>"; the short form has no tail.
                        var r = ParseRef(c2 >= 0 ? rest.Substring(0, c2) : rest);
                        if (r != null && !(r.Name == "Null" && r.CidText == "0")) info.Srf = r;
                    }
                }
                if (info.CidValid)
                {
                    if (model.ByCid.ContainsKey(info.CidText)) model.DuplicateCids.Add(info.CidText);
                    else model.ByCid[info.CidText] = info.Index;
                }
                model.Parts.Add(info);
            }
            foreach (var p in model.Parts)
                foreach (var l in p.Links)
                {
                    int child;
                    if (l != null && l.CidValid && model.ByCid.TryGetValue(l.CidText, out child) && child != p.Index && model.Parts[child].Parent < 0)
                    { model.Parts[child].Parent = p.Index; p.Children.Add(child); }
                }
            return model;
        }
        public int Root0()
        {
            for (int i = 0; i < Parts.Count; i++) if (Parts[i].Parent < 0) return i;
            return Parts.Count == 0 ? -1 : 0;
        }
        public static bool TryVector(string text, out Vector v)
        {
            v = new Vector(); if (text == null) return false;
            var parts = text.Split(',');
            if (parts.Length != 3) return false;
            double x, y, z;
            if (!ParseNumber(parts[0], out x) || !ParseNumber(parts[1], out y) || !ParseNumber(parts[2], out z)) return false;
            v = new Vector(x, y, z); return true;
        }
        public static bool TryRotation(string text, out Rotation q)
        {
            q = RotationMath.Identity; if (text == null) return false;
            var parts = text.Split(',');
            if (parts.Length != 4) return false;
            double x, y, z, w;
            if (!ParseNumber(parts[0], out x) || !ParseNumber(parts[1], out y) || !ParseNumber(parts[2], out z) || !ParseNumber(parts[3], out w)) return false;
            q = new Rotation(x, y, z, w); return true;
        }
        public static bool ParseNumber(string text, out double value)
        {
            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
