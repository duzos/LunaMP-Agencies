using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KspControl.Bridge
{
    internal sealed partial class Observations
    {
        private ShipConstruct observedEditorShip;
        private string editorGeneration;
        private static JObject PartDefinition(JObject args)
        {
            var name = (string)args?["partName"];
            if (string.IsNullOrEmpty(name) || name.Length > 256) throw new ArgumentException("invalid_part_name");
            var definition = PartLoader.LoadedPartsList?.FirstOrDefault(p => p != null && p.name == name);
            if (definition?.partPrefab == null) throw new InvalidOperationException("definition_unavailable");
            var result = ConfiguredPart(definition.partPrefab, false);
            result["provenance"] = "loaded_prefab_configuration_not_editor_instance";
            result["configurationVerifiedForConstruction"] = false;
            result["nativeConfigProvenance"] = "AvailablePart.partConfig_raw_definition_not_runtime_module_save";
            result["nativeConfig"] = (bool?)args?["includeNative"] == true ? NativeConfig(definition.partConfig) : Omitted();
            return result;
        }
        private JObject EditorSnapshot(ShipConstruct ship, JObject args)
        {
            if (!ReferenceEquals(ship, observedEditorShip)) { observedEditorShip = ship; editorGeneration = Guid.NewGuid().ToString("N"); }
            var offset = Offset(args); var limit = Math.Min(20, Limit(args));
            var parts = new JArray(); var root = ship.Parts.FirstOrDefault(p => p != null && p.parent == null);
            foreach (var part in ship.Parts.Skip(offset).Take(limit))
            {
                if (part == null) continue;
                var value = ConfiguredPart(part, true);
                if (root != null)
                {
                    value["position"] = Vector(root.transform.InverseTransformPoint(part.transform.position));
                    var q = Quaternion.Inverse(root.transform.rotation) * part.transform.rotation;
                    value["rotationQuaternion"] = new JArray(Finite(q.x), Finite(q.y), Finite(q.z), Finite(q.w));
                }
                value["parentPartId"] = part.parent == null ? null : part.parent.persistentId.ToString();
                value["stage"] = part.inverseStage;
                parts.Add(value);
            }
            var includeNative = (bool?)args?["includeNative"] == true;
            JObject native;
            if (!includeNative) native = Omitted();
            else if (ship.Parts.Count > 250) native = Unavailable("native_snapshot_part_limit");
            else native = NativeConfig(ship.SaveShip());
            return new JObject { ["name"] = Text(ship.shipName), ["parts"] = parts, ["partsTotal"] = ship.Parts.Count,
                ["offset"] = offset, ["nextOffset"] = offset + limit < ship.Parts.Count ? (JToken)new JValue(offset + limit) : JValue.CreateNull(),
                ["editorGeneration"] = editorGeneration, ["generationSemantics"] = "ShipConstruct_object_identity_only_not_edit_revision",
                ["mutationPreconditionSupported"] = false, ["coordinateFrame"] = "root_part_local_metres",
                ["rootPartId"] = root?.persistentId.ToString(), ["nativeConfig"] = native,
                ["nativeConfigProvenance"] = "ShipConstruct.SaveShip_current_editor_full_craft_not_part_prefab",
                ["provenance"] = "configured_editor_instances_current_observation" };
        }
        private static JObject ConfiguredPart(Part part, bool instance)
        {
            var nodes = new JArray();
            foreach (var node in (part.attachNodes ?? new List<AttachNode>()).Take(32)) if (node != null) nodes.Add(Node(node, instance));
            var resources = new JArray();
            foreach (PartResource resource in part.Resources)
            {
                if (resources.Count >= 32) break;
                resources.Add(new JObject { ["name"] = Text(resource.resourceName), ["amount"] = Finite(resource.amount),
                    ["maximum"] = Finite(resource.maxAmount), ["flowEnabled"] = resource.flowState });
            }
            var variants = new JArray();
            foreach (var name in (part.variants?.GetVariantNames() ?? new List<string>()).Take(16)) variants.Add(Text(name));
            return new JObject { ["name"] = Text(part.partInfo?.name), ["partId"] = instance ? part.persistentId.ToString() : null,
                ["attachNodes"] = nodes, ["surfaceAttachNode"] = part.srfAttachNode == null ? null : Node(part.srfAttachNode, instance),
                ["nodeCoordinateFrame"] = "part_local_metres", ["resources"] = resources, ["variants"] = variants,
                ["selectedVariant"] = part.variants == null ? (JToken)JValue.CreateNull() : new JValue(part.variants.GetCurrentVariantIndex()),
                ["nodeCount"] = part.attachNodes?.Count ?? 0, ["resourceCount"] = part.Resources.Count,
                ["nodeLimit"] = 32, ["resourceLimit"] = 32, ["variantLimit"] = 16,
                ["complete"] = (part.attachNodes?.Count ?? 0) <= 32 && part.Resources.Count <= 32 && (part.variants?.variantList?.Count ?? 0) <= 16,
                ["moduleConfiguration"] = "native_snapshot_required_unknown_modules_preserved_no_field_defaults_inferred" };
        }
        private static JObject Node(AttachNode node, bool instance) => new JObject
        {
            ["id"] = Text(node.id), ["type"] = node.nodeType.ToString(), ["position"] = Vector(node.position),
            ["orientation"] = Vector(node.orientation), ["size"] = node.size,
            ["attachedPartId"] = instance && node.attachedPart != null ? node.attachedPart.persistentId.ToString() : null
        };
        private static JArray Vector(Vector3 value) => new JArray(Finite(value.x), Finite(value.y), Finite(value.z));
        private static JObject Omitted() => Unavailable("not_requested");
        private static JObject Unavailable(string reason) => new JObject { ["available"] = false, ["reason"] = reason };
        private static JObject NativeConfig(ConfigNode node)
        {
            if (node == null) return Unavailable("native_config_unavailable");
            // Inspect the tree before ToString to bound output and avoid serializing unexpectedly huge mod nodes.
            var seen = new HashSet<ConfigNode>(); var pending = new Stack<Tuple<ConfigNode, int>>(); pending.Push(Tuple.Create(node, 0));
            long characters = 0;
            while (pending.Count != 0)
            {
                var entry = pending.Pop(); var current = entry.Item1;
                if (current == null || entry.Item2 > 32) return Unavailable("native_config_complexity_limit");
                if (!seen.Add(current) || seen.Count > 1024) return Unavailable("native_config_complexity_limit");
                characters += current.name?.Length ?? 0;
                if (characters > 64000) return Unavailable("native_config_size_limit");
                foreach (ConfigNode.Value value in current.values)
                { characters += (value.name?.Length ?? 0) + (value.value?.Length ?? 0) + 8; if (characters > 64000) return Unavailable("native_config_size_limit"); }
                foreach (ConfigNode child in current.nodes)
                { if (pending.Count >= 1024) return Unavailable("native_config_complexity_limit"); pending.Push(Tuple.Create(child, entry.Item2 + 1)); }
            }
            var text = node.ToString();
            if (text.Length > 64000) return Unavailable("native_config_size_limit");
            return new JObject { ["available"] = true, ["format"] = "KSP_ConfigNode", ["text"] = text,
                ["roundtripVerified"] = false, ["preserveUnknownNodes"] = true };
        }
    }
}
