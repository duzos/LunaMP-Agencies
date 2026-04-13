using LmpClient.Extensions;
using LmpClient.Utilities;
using System;

namespace LmpClient.VesselUtilities
{
    public class VesselSerializer
    {
        /// <summary>
        /// Deserialize a byte array into a protovessel
        /// </summary>
        public static ProtoVessel DeserializeVessel(byte[] data, int numBytes)
        {
            try
            {
                var vesselNode = data.DeserializeToConfigNode(numBytes);
                var configGuid = vesselNode?.GetValue("pid");

                return CreateSafeProtoVesselFromConfigNode(vesselNode, new Guid(configGuid));
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Error while deserializing vessel: {e}");
                return null;
            }
        }

        /// <summary>
        /// Serialize a protovessel into a byte array
        /// </summary>
        public static byte[] SerializeVessel(ProtoVessel protoVessel)
        {
            return PreSerializationChecks(protoVessel, out var configNode) ? configNode.Serialize() : new byte[0];
        }

        /// <summary>
        /// Serializes a vessel to a previous preallocated array (avoids garbage generation)
        /// </summary>
        public static void SerializeVesselToArray(ProtoVessel protoVessel, byte[] data, out int numBytes)
        {
            if (PreSerializationChecks(protoVessel, out var configNode))
            {
                configNode.SerializeToArray(data, out numBytes);
            }
            else
            {
                numBytes = 0;
            }
        }

        /// <summary>
        /// Creates a protovessel from a ConfigNode
        /// </summary>
        public static ProtoVessel CreateSafeProtoVesselFromConfigNode(ConfigNode inputNode, Guid protoVesselId)
        {
            try
            {
                //Cannot create a protovessel if HighLogic.CurrentGame is null as we don't have a CrewRoster
                //and the protopartsnapshot constructor needs it
                if (HighLogic.CurrentGame == null)
                    return null;

                // Strip part data that is not supported by this client before creating the ProtoVessel.
                // Entries from mods installed on the server but not on this client cause a
                // NullReferenceException inside ProtoVessel.Save() when KSP saves the game during routine UI
                // transitions (e.g. closing Mission Control), leaving the UI stuck in a half-closed state.
                StripUnknownPartData(inputNode, protoVesselId);

                //Cannot reuse the Protovessel to save memory garbage as it does not have any clear method :(
                return new ProtoVessel(inputNode, HighLogic.CurrentGame);
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Damaged vessel {protoVesselId}, exception: {e}");
                return null;
            }
        }

        /// <summary>
        /// Removes MODULE and RESOURCE nodes from each PART in a vessel ConfigNode when the corresponding
        /// type or definition is not present in this KSP installation.  Entries from mods on the server
        /// that are absent on this client cause a NullReferenceException inside ProtoVessel.Save() when KSP
        /// saves the game during routine UI transitions (e.g. closing Mission Control), leaving the UI stuck.
        /// </summary>
        private static void StripUnknownPartData(ConfigNode vesselNode, Guid vesselId)
        {
            if (vesselNode == null) return;

            var strippedModules = 0;
            var strippedResources = 0;

            // GetNodes returns an array copy so it is safe to call RemoveNode on the parent while iterating.
            foreach (var partNode in vesselNode.GetNodes("PART"))
            {
                foreach (var moduleNode in partNode.GetNodes("MODULE"))
                {
                    var moduleName = moduleNode.GetValue("name");
                    if (string.IsNullOrEmpty(moduleName)) continue;

                    if (AssemblyLoader.GetClassByName(typeof(PartModule), moduleName) == null)
                    {
                        partNode.RemoveNode(moduleNode);
                        strippedModules++;
                    }
                }

                foreach (var resourceNode in partNode.GetNodes("RESOURCE"))
                {
                    var resourceName = resourceNode.GetValue("name");
                    if (string.IsNullOrEmpty(resourceName)) continue;

                    if (!PartResourceLibrary.Instance.resourceDefinitions.Contains(resourceName))
                    {
                        partNode.RemoveNode(resourceNode);
                        strippedResources++;
                    }
                }
            }

            if (strippedModules > 0)
                LunaLog.LogWarning($"[LMP]: Vessel {vesselId}: stripped {strippedModules} PartModule(s) from mods not installed on this client.");
            if (strippedResources > 0)
                LunaLog.LogWarning($"[LMP]: Vessel {vesselId}: stripped {strippedResources} resource(s) from mods not installed on this client.");
        }

        #region Private methods

        private static bool PreSerializationChecks(ProtoVessel protoVessel, out ConfigNode configNode)
        {
            configNode = new ConfigNode();

            if (protoVessel == null)
            {
                LunaLog.LogError("[LMP]: Cannot serialize a null protovessel");
                return false;
            }

            try
            {
                protoVessel.Save(configNode);
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Error while saving vessel: {e}");
                return false;
            }

            var vesselId = new Guid(configNode.GetValue("pid"));

            //Defend against NaN orbits
            if (configNode.VesselHasNaNPosition())
            {
                LunaLog.LogError($"[LMP]: Vessel {vesselId} has NaN position");
                return false;
            }

            //Do not send the maneuver nodes
            RemoveManeuverNodesFromProtoVessel(configNode);
            return true;
        }


        #region Config node fixing

        /// <summary>
        /// Removes maneuver nodes from the vessel
        /// </summary>
        private static void RemoveManeuverNodesFromProtoVessel(ConfigNode vesselNode)
        {
            var flightPlanNode = vesselNode?.GetNode("FLIGHTPLAN");
            flightPlanNode?.ClearData();
        }

        #endregion

        #endregion
    }
}
