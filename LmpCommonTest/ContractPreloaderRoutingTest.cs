using System;
using LmpClient;
using LmpClient.Harmony;
using LmpCommon.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;

public static class LunaLog { public static void Log(string message) { } public static void LogWarning(string message) { } public static void LogError(string message) { } }
namespace Contracts
{
    public class ContractSystem
    {
        public static ContractSystem Instance;
        public static Type GetParameterType(string name) => typeof(object);
    }
}
namespace LmpCommonTest
{
    [TestClass, DoNotParallelize]
    public class ContractPreloaderRoutingTest
    {
        private static ConfigNode Contract(string type)
        {
            var node = new ConfigNode("CONTRACT");
            if (type != null) node.AddValue("type", type);
            node.AddValue("targetBody", type == "ExplorationContract" ? "1" : "Kerbin");
            return node;
        }
        [TestCleanup] public void Reset() { Contracts.ContractSystem.Instance = null; MainSystem.NetworkState = ClientState.Disconnected; }
        [TestMethod] public void StockContractsNeverReachConfiguredPreloaderEvenBeforeStockSingletonExists()
        {
            MainSystem.NetworkState = ClientState.Connected; Contracts.ContractSystem.Instance = null;
            var preload = new ConfigNode(); var stock = Contract("ExplorationContract"); var configured = Contract("ConfiguredContract");
            var other = new ConfigNode("STATE"); var untyped = Contract(null);
            preload.AddNode(stock); preload.AddNode(configured); preload.AddNode(other); preload.AddNode(untyped);
            ContractPreLoader_Filter.Prefix(null, preload);
            CollectionAssert.AreEqual(new[] { configured, other, untyped }, preload.nodes.ToArray());
            Assert.AreEqual("1", stock.GetValue("targetBody")); // The stock copy is untouched.
            Assert.IsFalse(ContractPreLoader_Filter.IsConfiguredContractNode(stock));
            Assert.IsTrue(ContractPreLoader_Filter.IsConfiguredContractNode(configured));
            Assert.IsFalse(ContractPreLoader_Filter.IsConfiguredContractNode(untyped));
        }
        [TestMethod] public void ConfiguredContractsStillPassNormalValidation()
        {
            MainSystem.NetworkState = ClientState.Connected; Contracts.ContractSystem.Instance = new Contracts.ContractSystem();
            var preload = new ConfigNode(); var configured = Contract("ConfiguredContract"); preload.AddNode(configured);
            ContractPreLoader_Filter.Prefix(null, preload);
            Assert.AreSame(configured,preload.nodes[0]); Assert.AreEqual(1,preload.nodes.Count);
        }
        [TestMethod] public void OfflineLoadIsUntouched()
        {
            MainSystem.NetworkState = ClientState.Disconnected;
            var preload = new ConfigNode(); var stock = Contract("ExplorationContract"); preload.AddNode(stock);
            ContractPreLoader_Filter.Prefix(null, preload);
            Assert.AreSame(stock,preload.nodes[0]);
        }
    }
}
