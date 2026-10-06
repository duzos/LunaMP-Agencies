using System;
using System.Diagnostics;
using System.Net;
using System.Linq;
using System.Threading;
using Lidgren.Network;
using LmpCommon.Message;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class FragmentedVesselProtoTest
    {
        [DataTestMethod]
        [DataRow(false, false, false)]
        [DataRow(true, false, false)]
        [DataRow(false, true, false)]
        [DataRow(true, true, false)]
        [DataRow(false, false, true)]
        public void FragmentedProtoPreservesExactBitLength(bool forceReload, bool economyTail, bool byteAlignedControl)
        {
            var app = "fragment-test-" + Guid.NewGuid();
            var server = new NetServer(new NetPeerConfiguration(app) { Port = 0, LocalAddress = IPAddress.Loopback });
            var client = new NetClient(new NetPeerConfiguration(app) { LocalAddress = IPAddress.Loopback });
            try
            {
                server.Start(); client.Start(); client.Connect(IPAddress.Loopback.ToString(), server.Port);
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 5000 && (client.ConnectionStatus != NetConnectionStatus.Connected || server.ConnectionsCount == 0))
                {
                    while (server.ReadMessage() != null) { }
                    while (client.ReadMessage() != null) { }
                    Thread.Sleep(5);
                }
                Assert.AreEqual(NetConnectionStatus.Connected, client.ConnectionStatus);
                var factory = new ServerMessageFactory();
                var source = factory.CreateNewMessageData<VesselProtoMsgData>();
                var bytes = new byte[24000]; new Random(417).NextBytes(bytes);
                source.VesselId = Guid.NewGuid(); source.Data = (byte[])bytes.Clone(); source.NumBytes = bytes.Length;
                source.ForceReload = forceReload; source.Reason = "Resync";
                if (economyTail) source.EconomyLaunchId = Guid.NewGuid();
                var output = server.CreateMessage();
                if (byteAlignedControl) output.Write(bytes); else source.Serialize(output);
                var expectedBits = output.LengthBits;
                Assert.IsTrue(output.LengthBytes > server.Configuration.MaximumTransmissionUnit);
                Assert.AreEqual(byteAlignedControl ? 0 : 1, expectedBits % 8);
                server.SendMessage(output, server.Connections[0], NetDeliveryMethod.ReliableOrdered, 0);
                NetIncomingMessage received = null;
                timer.Restart();
                while (timer.ElapsedMilliseconds < 5000)
                {
                    var message = client.ReadMessage();
                    if (message != null && message.MessageType == NetIncomingMessageType.Data) { received = message; break; }
                    Thread.Sleep(5);
                }
                Assert.IsNotNull(received, "Fragmented message did not arrive.");
                Assert.AreEqual(expectedBits, received.LengthBits);
                if (byteAlignedControl)
                {
                    CollectionAssert.AreEqual(bytes, received.ReadBytes(bytes.Length));
                    return;
                }
                var target = factory.CreateNewMessageData<VesselProtoMsgData>();
                target.Deserialize(received);
                Assert.AreEqual(expectedBits, received.LengthBits);
                Assert.AreEqual(forceReload, target.ForceReload);
                Assert.AreEqual(source.EconomyLaunchId, target.EconomyLaunchId);
                Assert.AreEqual(bytes.Length, target.NumBytes);
                CollectionAssert.AreEqual(bytes, target.Data.Take(target.NumBytes).ToArray());
            }
            finally
            {
                // Let reliable acknowledgements settle before tearing down both peers.
                Thread.Sleep(250);
                client.Shutdown("Test complete"); server.Shutdown("Test complete");
                SpinWait.SpinUntil(() => client.Status == NetPeerStatus.NotRunning && server.Status == NetPeerStatus.NotRunning, 3000);
            }
        }
    }
}
