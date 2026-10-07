using System;
using System.Collections.Generic;
using System.Globalization;
namespace KspControl.EditorModel
{
    public struct PartRef
    {
        public string Name;
        public uint Cid;
        public PartRef(string name, uint cid) { Name = name; Cid = cid; }
        public override string ToString() { return Name + "_" + Cid.ToString(CultureInfo.InvariantCulture); }
    }
    public sealed class StructuralAttachNode
    {
        public string NodeId { get; set; }
        public PartRef Partner { get; set; }
        /// <summary>Position of this part's own node (written as x|y|z), used only in the vector form.</summary>
        public Vector NodePosition { get; set; }
    }
    public struct StagingFields
    {
        public int Istg, Dstg, Sidx, Sqor, SepI, Attm;
        public StagingFields(int istg, int dstg, int sidx, int sqor, int sepI, int attm) { Istg = istg; Dstg = dstg; Sidx = sidx; Sqor = sqor; SepI = sepI; Attm = attm; }
    }
    public sealed class StructuralPart
    {
        public string SourceId { get; set; }
        public string Name { get; set; }
        public uint Cid { get; set; }
        public uint PersistentId { get; set; }
        public Vector Position { get; set; }
        public Rotation Rotation { get; set; } = RotationMath.Identity;
        public Vector AttPos0 { get; set; }
        public Rotation AttRot0 { get; set; } = RotationMath.Identity;
        public string Mir { get; set; } = "1,1,1";
        public string SymMethod { get; set; } = "Radial";
        public string AutostrutMode { get; set; } = "Off";
        public bool RigidAttachment { get; set; }
        public StagingFields Staging { get; set; }
        public List<PartRef> Links { get; } = new List<PartRef>();
        public List<PartRef> Sym { get; } = new List<PartRef>();
        public PartRef? SurfaceParent { get; set; }
        public List<StructuralAttachNode> AttachNodes { get; } = new List<StructuralAttachNode>();
        public PartRef Ref { get { return new PartRef(Name, Cid); } }
    }
    public sealed class StructuralHeader
    {
        public string Ship { get; set; }
        public string Version { get; set; }
        public string Description { get; set; } = "";
        public string Type { get; set; } = "VAB";
        public Vector Size { get; set; }
        public string SteamPublishedFileId { get; set; } = "0";
        public uint PersistentId { get; set; }
        public string Rot { get; set; } = "0,0,0,0"; // KSP-built crafts write 0,0,0,0 (33-log 18:51)
        public string MissionFlag { get; set; }
        public string VesselType { get; set; }
        public string ModVersions { get; set; }
    }
    /// <summary>A structure-only craft: transforms, links and staging integers, with no MODULE/RESOURCE/EVENTS/ACTIONS/PARTDATA nodes.</summary>
    public sealed class StructuralCraft
    {
        public StructuralHeader Header { get; set; } = new StructuralHeader();
        public List<StructuralPart> Parts { get; } = new List<StructuralPart>();
        /// <summary>Emit attN as "node,partner_cid" without a vector (form pending S0e).</summary>
        public bool VectorLessAttN { get; set; }
        public ConfigNode ToConfigNode()
        {
            var root = new ConfigNode("");
            var h = Header;
            root.AddValue("ship", h.Ship ?? "");
            root.AddValue("version", h.Version ?? "");
            root.AddValue("description", h.Description ?? "");
            root.AddValue("type", h.Type ?? "VAB");
            root.AddValue("size", RotationMath.Format(h.Size));
            root.AddValue("steamPublishedFileId", h.SteamPublishedFileId ?? "0");
            root.AddValue("persistentId", h.PersistentId.ToString(CultureInfo.InvariantCulture));
            root.AddValue("rot", h.Rot ?? "0,0,0,0");
            root.AddValue("missionFlag", h.MissionFlag ?? "");
            root.AddValue("vesselType", h.VesselType ?? "");
            if (h.ModVersions != null) root.AddValue("_modVersions", h.ModVersions);
            foreach (var p in Parts)
            {
                var n = root.AddNode("PART");
                n.AddValue("part", p.Ref.ToString());
                n.AddValue("partName", "Part");
                n.AddValue("persistentId", p.PersistentId.ToString(CultureInfo.InvariantCulture));
                n.AddValue("pos", RotationMath.Format(p.Position));
                n.AddValue("attPos", "0,0,0");
                n.AddValue("attPos0", RotationMath.Format(p.AttPos0));
                n.AddValue("rot", RotationMath.Format(p.Rotation));
                n.AddValue("attRot", "0,0,0,1");
                n.AddValue("attRot0", RotationMath.Format(p.AttRot0));
                n.AddValue("mir", p.Mir);
                n.AddValue("symMethod", p.SymMethod);
                n.AddValue("autostrutMode", p.AutostrutMode);
                n.AddValue("rigidAttachment", p.RigidAttachment ? "True" : "False");
                var s = p.Staging;
                n.AddValue("istg", s.Istg.ToString(CultureInfo.InvariantCulture));
                n.AddValue("resPri", "0");
                n.AddValue("dstg", s.Dstg.ToString(CultureInfo.InvariantCulture));
                n.AddValue("sidx", s.Sidx.ToString(CultureInfo.InvariantCulture));
                n.AddValue("sqor", s.Sqor.ToString(CultureInfo.InvariantCulture));
                n.AddValue("sepI", s.SepI.ToString(CultureInfo.InvariantCulture));
                n.AddValue("attm", s.Attm.ToString(CultureInfo.InvariantCulture));
                n.AddValue("modCost", "0");
                n.AddValue("modMass", "0");
                n.AddValue("modSize", "0,0,0");
                foreach (var l in p.Links) n.AddValue("link", l.ToString());
                foreach (var sy in p.Sym) n.AddValue("sym", sy.ToString());
                if (p.SurfaceParent.HasValue) n.AddValue("srfN", "srfAttach," + p.SurfaceParent.Value);
                foreach (var a in p.AttachNodes)
                    n.AddValue("attN", a.NodeId + "," + a.Partner + (VectorLessAttN ? "" : "_" + RotationMath.FormatPipe(a.NodePosition)));
            }
            return root;
        }
        public string ToText(string newLine = "\r\n") { return ConfigText.Print(ToConfigNode(), newLine); }
    }
}
