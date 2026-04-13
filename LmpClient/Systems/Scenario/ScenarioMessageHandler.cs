using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Extensions;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Scenario;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using System;
using System.Collections.Concurrent;

namespace LmpClient.Systems.Scenario
{
    public class ScenarioMessageHandler : SubSystem<ScenarioSystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is ScenarioBaseMsgData msgData)) return;

            switch (msgData.ScenarioMessageType)
            {
                case ScenarioMessageType.Data:
                    QueueAllReceivedScenarios(msgData);
                    break;
                case ScenarioMessageType.Proto:
                    var data = (ScenarioProtoMsgData)msgData;
                    QueueScenarioBytes(data.ScenarioData.Module, data.ScenarioData.Data, data.ScenarioData.NumBytes);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static void QueueAllReceivedScenarios(ScenarioBaseMsgData msgData)
        {
            var data = (ScenarioDataMsgData)msgData;
            for (var i = 0; i < data.ScenarioCount; i++)
            {
                QueueScenarioBytes(data.ScenariosData[i].Module, data.ScenariosData[i].Data, data.ScenariosData[i].NumBytes);
            }

            if (MainSystem.NetworkState < ClientState.ScenariosSynced)
                MainSystem.NetworkState = ClientState.ScenariosSynced;
        }

        private static void QueueScenarioBytes(string scenarioModule, byte[] scenarioData, int numBytes)
        {
            var scenarioNode = scenarioData.DeserializeToConfigNode(numBytes);
            if (scenarioNode != null)
            {
                if (scenarioModule == "ContractSystem")
                {
                    var contracts = scenarioNode.GetNode("CONTRACTS")?.GetNodes("CONTRACT") ?? new ConfigNode[0];
                    var finishedContracts = scenarioNode.GetNode("CONTRACTS_FINISHED")?.GetNodes("CONTRACT") ?? new ConfigNode[0];
                    LunaLog.Log($"[ShareContracts]: Received ContractSystem from server — {contracts.Length} in CONTRACTS, {finishedContracts.Length} in CONTRACTS_FINISHED.");
                    foreach (var contract in contracts)
                    {
                        LunaLog.Log($"[ShareContracts]: Contract - GUID: {contract.GetValue("guid")} | Type: {contract.GetValue("type")} | State: {contract.GetValue("state")}");
                    }
                    foreach (var contract in finishedContracts)
                    {
                        LunaLog.Log($"[ShareContracts]: Finished Contract - GUID: {contract.GetValue("guid")} | Type: {contract.GetValue("type")} | State: {contract.GetValue("state")}");
                    }
                }

                var entry = new ScenarioEntry
                {
                    ScenarioModule = scenarioModule,
                    ScenarioNode = scenarioNode
                };
                System.ScenarioQueue.Enqueue(entry);
            }
            else
            {
                LunaLog.LogError($"[LMP]: Scenario data has been lost for {scenarioModule}");
                byte[] rawCopy = null;
                if (scenarioData != null && numBytes > 0)
                {
                    var len = global::System.Math.Min(numBytes, scenarioData.Length);
                    rawCopy = new byte[len];
                    global::System.Buffer.BlockCopy(scenarioData, 0, rawCopy, 0, len);
                }

                System.ScenarioQueue.Enqueue(new ScenarioEntry
                {
                    ScenarioModule = scenarioModule,
                    ScenarioNode = null,
                    RawScenarioBytes = rawCopy,
                    RawNumBytes = numBytes
                });
            }
        }
    }
}