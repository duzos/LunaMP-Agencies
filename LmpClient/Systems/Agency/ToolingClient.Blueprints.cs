using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LmpClient.Extensions;
using LmpCommon.Agency;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// The KSP-bound half of tooling blueprints (plan 40): editor capture, craft hashing, the missing-part check, the craft file write and the
    /// editor load. Not linked into LmpCommonTest; ToolingClientTestEnvironment stubs these members. The load state machine lives in ToolingClient.cs.
    /// </summary>
    public static partial class ToolingClient
    {
        /// <summary>The open editor craft's bytes (SaveShip().Serialize()), facility and name. False when there is no craft or its fingerprint differs from the current manifest.</summary>
        private static bool CaptureEditorBlueprint(out byte[] bytes, out string editor, out string name)
        {
            bytes = null; editor = null; name = null;
            if (!TrySerializeEditorCraft(out var node, out var data)) return false;
            name = node.GetValue("ship");
            if (string.IsNullOrEmpty(name)) name = EditorLogic.fetch.ship.shipName;
            editor = node.GetValue("type");
            if (editor != "VAB" && editor != "SPH") editor = EditorDriver.editorFacility == EditorFacility.SPH ? "SPH" : "VAB";
            // A blueprint whose parts differ from the tooled manifest would be skipped by the server anyway; send none (plan 40 R1.10 L2).
            if (ToolingPolicy.Fingerprint(ToolingManifestBuilder.FromConfig(node, null)) != ToolingPolicy.Fingerprint(CurrentManifest())) return false;
            bytes = data;
            return true;
        }

        /// <summary>SHA-256 (lowercase hex) of the open editor craft's blueprint bytes, for comparing with the saved blueprint.</summary>
        private static bool TryEditorBlueprintHash(out string hash)
        {
            hash = null;
            if (!TrySerializeEditorCraft(out _, out var data)) return false;
            hash = HexHash(data);
            return true;
        }

        private static bool TrySerializeEditorCraft(out ConfigNode node, out byte[] data)
        {
            node = null; data = null;
            var ship = HighLogic.LoadedSceneIsEditor ? EditorLogic.fetch?.ship : null;
            if (ship?.parts == null || ship.parts.Count == 0) return false;
            node = ship.SaveShip();
            if (node == null) return false;
            data = node.Serialize();
            return data != null && data.Length > 0 && data.Length <= ToolingLimits.MaxToolingBlueprintBytes;
        }

        /// <summary>Part names in the blueprint (including stored inventory parts) that this install does not have (PartLoader). Empty when everything can load.</summary>
        private static string[] MissingBlueprintParts(byte[] bytes)
        {
            var root = ConfigNode.Parse(Encoding.UTF8.GetString(bytes)) ?? throw new InvalidOperationException("Saved craft is unreadable.");
            var missing = new SortedSet<string>(StringComparer.Ordinal);
            var stack = new Stack<ConfigNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                foreach (ConfigNode child in node.nodes)
                {
                    stack.Push(child);
                    if (child.name != "PART") continue;
                    string name;
                    try { name = ToolingManifestBuilder.PartName(child); }
                    catch (InvalidOperationException) { continue; }
                    if (PartLoader.getPartInfoByName(name) == null) missing.Add(name);
                }
            }
            return missing.ToArray();
        }

        /// <summary>The fingerprint of a blueprint's parts. Call only after <see cref="MissingBlueprintParts"/> is empty, because the manifest builder needs every part.</summary>
        private static string BlueprintFingerprint(byte[] bytes) =>
            ToolingPolicy.Fingerprint(ToolingManifestBuilder.FromConfig(ConfigNode.Parse(Encoding.UTF8.GetString(bytes)), null));

        /// <summary>Writes the fetched blueprint to the save's Ships library (Tooled-&lt;name&gt;-&lt;fp8&gt;.craft) and returns the path.</summary>
        private static string WriteTooledCraftFile(ToolingBlueprintInfo info, byte[] bytes)
        {
            var fingerprint8 = info.Fingerprint.Substring(0, Math.Min(8, info.Fingerprint.Length));
            var hash8 = info.Hash.Substring(0, Math.Min(8, info.Hash.Length));
            return TradeClient.WriteCraftFile(info.Editor, "Tooled-" + TradeClient.CraftFileName(info.Name, "Tooled craft") + "-" + fingerprint8,
                "Tooled-" + fingerprint8 + "-" + hash8, bytes, info.Hash);
        }

        /// <summary>
        /// Loads a craft file. In the editor it always loads in place (stock KSP loads an SPH craft in the VAB and vice versa; never re-enter EDITOR
        /// from EDITOR). From the Space Center it enters the blueprint's facility.
        /// </summary>
        private static void LoadTooledCraftFile(string path, string editor)
        {
            if (HighLogic.LoadedSceneIsEditor) EditorLogic.LoadShipFromFile(path);
            else if (HighLogic.LoadedScene == GameScenes.SPACECENTER) EditorDriver.StartAndLoadVessel(path, editor == "SPH" ? EditorFacility.SPH : EditorFacility.VAB);
            else throw new InvalidOperationException("Return to the Space Center to load a design.");
        }
    }
}
