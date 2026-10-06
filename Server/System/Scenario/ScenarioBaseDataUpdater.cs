using Server.Diagnostics;
using LunaConfigNode.CfgNode;
using Server.Log;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;

namespace Server.System.Scenario
{
    public partial class ScenarioDataUpdater
    {
        #region Semaphore

        /// <summary>
        /// To not overwrite our own data we use a lock
        /// </summary>
        private static readonly ConcurrentDictionary<string, object> Semaphore = new ConcurrentDictionary<string, object>();

        #endregion

        /// <summary>
        /// Creates a ConfigNode from raw bytes, stripping the outer { } braces that KSP's
        /// ConfigNode.WriteNode() adds. LunaConfigNode's parser wraps braced content in an
        /// unnamed child node, which causes GetValue() on the root to return null.
        /// </summary>
        internal static ConfigNode ParseClientConfigNode(byte[] data, int numBytes, string nodeName)
        {
            return ParseClientConfigNode(Encoding.UTF8.GetString(data, 0, numBytes), nodeName);
        }

        internal static ConfigNode ParseClientConfigNode(string raw, string nodeName)
        {
            var trimmed = raw.Trim();

            // KSP serializes unnamed ConfigNodes as "{\n\tkey = val\n}" — strip the wrapper
            if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
                trimmed = trimmed.Substring(1, trimmed.Length - 2);

            return new ConfigNode(trimmed) { Name = nodeName };
        }

        /// <summary>
        /// Raw updates a scenario in the dictionary, stripping outer { } braces
        /// that KSP's ConfigNode serializer adds (same fix as ParseClientConfigNode).
        /// </summary>
        public static void RawConfigNodeInsertOrUpdate(string scenarioModule, string scenarioAsConfigNode)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var scenario = ParseClientConfigNode(scenarioAsConfigNode, scenarioModule);
                    lock (Semaphore.GetOrAdd(scenarioModule, new object()))
                    {
                        ScenarioStoreSystem.CurrentScenarios.AddOrUpdate(scenarioModule, scenario, (key, existingVal) => scenario);
                    }
                    PlaytestDiagnostics.Write("scenario.apply", () => $"module={scenarioModule} route=global result=applied-in-memory");
                }
                catch (Exception e)
                {
                    PlaytestDiagnostics.Write("scenario.apply", () => $"module={scenarioModule} route=global result=failed errorType={e.GetType().Name}");
                    LunaLog.Warning($"Failed to upsert global scenario {scenarioModule}: {e.GetType().Name}");
                }
            });
        }
    }
}
