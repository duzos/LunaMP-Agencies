using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Interface;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Plugin;
using Server.Server;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class AgenciesBuildHandshakeTest
    {
        private const long Untouched = -1;

        private static string OlderReason(string have) =>
            $"Agencies build mismatch: server is on agencies.{AgenciesBuild.Number}, you have {have}. Update from {AgenciesBuild.ReleasesPage}.";

        /// <summary>Records every client whose message reaches the plugin hook.</summary>
        private sealed class MessageProbe : ILmpPlugin
        {
            public List<ClientStructure> Received { get; } = new List<ClientStructure>();
            public void OnUpdate() { }
            public void OnServerStart() { }
            public void OnServerStop() { }
            public void OnClientConnect(ClientStructure client) { }
            public void OnClientAuthenticated(ClientStructure client) { }
            public void OnClientDisconnect(ClientStructure client) { }
            public void OnMessageReceived(ClientStructure client, IClientMessageBase messageData) => Received.Add(client);
            public void OnMessageSent(ClientStructure client, IServerMessageBase messageData) { }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly AgencyTestScope _scope = new AgencyTestScope();
            private readonly KeyValuePair<IPEndPoint, ClientStructure>[] _clients = ServerContext.Clients.ToArray();
            private readonly bool _oldModControl = GeneralSettings.SettingsStore.ModControl;
            private readonly int _oldMaxPlayers = GeneralSettings.SettingsStore.MaxPlayers;
            private readonly Func<Task> _oldDelay = HandshakeSystem.MismatchDisconnectDelay;
            private readonly NetClient _peer = new NetClient(new NetPeerConfiguration("agencies-build-handshake"));
            private int _port = 32400;

            private readonly List<ILmpPlugin> _plugins = (List<ILmpPlugin>)typeof(LmpPluginHandler).GetProperty("LoadedPlugins", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

            public HandshakeSystem Handshake { get; } = new HandshakeSystem();
            public MessageReceiver Receiver { get; } = new MessageReceiver();
            public MessageProbe Probe { get; } = new MessageProbe();

            public Fixture()
            {
                _plugins.Add(Probe);
                ServerContext.Clients.Clear();
                GeneralSettings.SettingsStore.ModControl = false;
                GeneralSettings.SettingsStore.MaxPlayers = 20;
                // Keeps the server from looking empty, so a disconnect never runs the last-client backup.
                NewClient();
            }

            public ClientStructure NewClient()
            {
                var connection = (NetConnection)RuntimeHelpers.GetUninitializedObject(typeof(NetConnection));
                typeof(NetConnection).GetField("m_remoteEndPoint", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(connection, new IPEndPoint(IPAddress.Loopback, _port++));
                var client = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));
                typeof(ClientStructure).GetField("<Connection>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, connection);
                typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, new ConcurrentQueue<IServerMessageBase>());
                client.PlayerName = "Unknown"; // the constructor default, which an uninitialized object skips
                client.LastReceiveTime = Untouched;
                client.ConnectionStatus = ConnectionStatus.Connected;
                ServerContext.Clients[client.Endpoint] = client;
                return client;
            }

            public HandshakeRequestMsgData Request(string name, int build)
            {
                var data = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                data.PlayerName = name;
                data.UniqueIdentifier = "id-" + name;
                data.KspVersion = "1.12.5";
                data.AgenciesBuild = build;
                return data;
            }

            /// <summary>The request as it arrives from the network, so it goes through the real deserialization and routing.</summary>
            public NetIncomingMessage Packet(string name, int build)
            {
                var message = ServerContext.ClientMessageFactory.CreateNew<HandshakeCliMsg, HandshakeRequestMsgData>();
                var data = (HandshakeRequestMsgData)message.Data;
                data.PlayerName = name;
                data.UniqueIdentifier = "id-" + name;
                data.KspVersion = "1.12.5";
                data.AgenciesBuild = build;
                var output = _peer.CreateMessage(message.GetMessageSize());
                message.Serialize(output);
                message.Recycle();
                output.Position = 0;
                // Lidgren only exposes this to its own test project.
                var create = typeof(NetPeer).GetMethod("CreateIncomingMessage", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(NetIncomingMessageType), typeof(byte[]) }, null);
                var input = (NetIncomingMessage)create.Invoke(_peer, new object[] { NetIncomingMessageType.Data, output.ReadBytes(output.LengthBytes) });
                input.LengthBits = output.LengthBits;
                return input;
            }

            public void Dispose()
            {
                _plugins.Remove(Probe);
                HandshakeSystem.MismatchDisconnectDelay = _oldDelay;
                GeneralSettings.SettingsStore.ModControl = _oldModControl;
                GeneralSettings.SettingsStore.MaxPlayers = _oldMaxPlayers;
                ServerContext.Clients.Clear();
                foreach (var pair in _clients) ServerContext.Clients[pair.Key] = pair.Value;
                _scope.Dispose();
            }
        }

        private static HandshakeReplyMsgData[] Replies(ClientStructure client) => client.SendMessageQueue.Select(m => m.Data).OfType<HandshakeReplyMsgData>().ToArray();

        [TestMethod]
        public void CheckAgenciesBuildAcceptsOnlyTheExactBuild()
        {
            Assert.IsTrue(HandshakeSystem.CheckAgenciesBuild(AgenciesBuild.Number, out var reason));
            Assert.AreEqual(string.Empty, reason);
            Assert.IsFalse(HandshakeSystem.CheckAgenciesBuild(AgenciesBuild.Number + 1, out _));
            Assert.IsFalse(HandshakeSystem.CheckAgenciesBuild(0, out _));
        }

        [TestMethod]
        public void CheckAgenciesBuildExplainsAClientThatSendsNoBuild()
        {
            Assert.IsFalse(HandshakeSystem.CheckAgenciesBuild(0, out var reason));
            StringAssert.Contains(reason, "agencies.1 or older");
            StringAssert.Contains(reason, AgenciesBuild.ReleasesPage);
            Assert.AreEqual(OlderReason("agencies.1 or older"), reason);
        }

        [TestMethod]
        public void CheckAgenciesBuildNamesAnOlderClientBuild()
        {
            Assert.IsFalse(HandshakeSystem.CheckAgenciesBuild(1, out var reason));
            Assert.AreEqual(OlderReason("agencies.1"), reason);
            StringAssert.Contains(reason, AgenciesBuild.ReleasesPage);
        }

        [TestMethod]
        public void CheckAgenciesBuildTellsANewerClientThatTheServerNeedsUpdating()
        {
            var newer = AgenciesBuild.Number + 4;
            Assert.IsFalse(HandshakeSystem.CheckAgenciesBuild(newer, out var reason));
            StringAssert.Contains(reason, "server needs updating");
            Assert.AreEqual($"Agencies build mismatch: you have agencies.{newer} but the server is on agencies.{AgenciesBuild.Number}; the server needs updating.", reason);
        }

        [TestMethod]
        public void MismatchedRequestIsRejectedWithReplyFourAndNoAuthentication()
        {
            using (var f = new Fixture())
            {
                HandshakeSystem.MismatchDisconnectDelay = () => new TaskCompletionSource<bool>().Task;
                var client = f.NewClient();

                f.Handshake.HandleHandshakeRequest(client, f.Request("Rocketeer", 0));

                Assert.IsTrue(client.HandshakeRejected);
                Assert.IsFalse(client.Authenticated);
                Assert.AreEqual(Guid.Empty, client.AgencyId);
                Assert.AreEqual("Unknown", client.PlayerName);
                Assert.IsFalse(client.DisconnectClient, "other systems set DisconnectClient on live clients; the mismatch path must not");
                Assert.AreEqual(ConnectionStatus.Connected, client.ConnectionStatus, "the disconnect is delayed so the reply can arrive");
                Assert.AreEqual(0, AgencyStore.Agencies.Count);

                var replies = Replies(client);
                Assert.AreEqual(1, replies.Length);
                Assert.AreEqual(HandshakeReply.AgenciesBuildMismatch, replies[0].Response);
                Assert.AreEqual(OlderReason("agencies.1 or older"), replies[0].Reason);
                Assert.AreEqual(AgenciesBuild.Number, replies[0].ServerAgenciesBuild);
            }
        }

        [TestMethod]
        public void BuildMismatchIsReportedBeforeAnyUsernameProblem()
        {
            using (var f = new Fixture())
            {
                HandshakeSystem.MismatchDisconnectDelay = () => new TaskCompletionSource<bool>().Task;
                var client = f.NewClient();

                f.Handshake.HandleHandshakeRequest(client, f.Request("not a valid name!", AgenciesBuild.Number + 1));

                var replies = Replies(client);
                Assert.AreEqual(1, replies.Length);
                Assert.AreEqual(HandshakeReply.AgenciesBuildMismatch, replies[0].Response);
                Assert.IsTrue(client.HandshakeRejected);
            }
        }

        [TestMethod]
        public void ServerFullIsStillCheckedBeforeTheBuild()
        {
            using (var f = new Fixture())
            {
                GeneralSettings.SettingsStore.MaxPlayers = 0;
                HandshakeSystem.MismatchDisconnectDelay = () => new TaskCompletionSource<bool>().Task;
                var client = f.NewClient();

                f.Handshake.HandleHandshakeRequest(client, f.Request("Rocketeer", 0));

                var replies = Replies(client);
                Assert.AreEqual(1, replies.Length);
                Assert.AreEqual(HandshakeReply.ServerFull, replies[0].Response);
                Assert.IsFalse(client.HandshakeRejected, "server full keeps its own disconnect path");
            }
        }

        [TestMethod]
        public void MatchingBuildAuthenticatesAndTheReplyCarriesTheServerBuild()
        {
            using (var f = new Fixture())
            {
                var client = f.NewClient();

                f.Handshake.HandleHandshakeRequest(client, f.Request("Rocketeer", AgenciesBuild.Number));

                Assert.IsFalse(client.HandshakeRejected);
                Assert.IsTrue(client.Authenticated);
                Assert.AreNotEqual(Guid.Empty, client.AgencyId);
                Assert.AreEqual("Rocketeer", client.PlayerName);
                var replies = Replies(client);
                Assert.AreEqual(1, replies.Length);
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, replies[0].Response);
                Assert.AreEqual(AgenciesBuild.Number, replies[0].ServerAgenciesBuild);
            }
        }

        [TestMethod]
        public void SecondRequestWithTheCorrectBuildIsIgnoredWhenDeliveredThroughReceiveCallback()
        {
            using (var f = new Fixture())
            {
                HandshakeSystem.MismatchDisconnectDelay = () => new TaskCompletionSource<bool>().Task;
                var client = f.NewClient();

                f.Receiver.ReceiveCallback(client, f.Packet("Rocketeer", 0));
                Assert.IsTrue(client.HandshakeRejected, "the mismatched request must travel the real receive path");
                var queued = client.SendMessageQueue.Count;
                Assert.AreEqual(1, queued);
                Assert.AreEqual(1, f.Probe.Received.Count(c => ReferenceEquals(c, client)), "the first request reaches the plugin hook");
                client.LastReceiveTime = Untouched;

                f.Receiver.ReceiveCallback(client, f.Packet("Rocketeer", AgenciesBuild.Number));

                Assert.AreEqual(1, f.Probe.Received.Count(c => ReferenceEquals(c, client)), "a rejected client's packets are dropped before the plugin hook");
                Assert.AreEqual(Untouched, client.LastReceiveTime, "a rejected client's packets are dropped before anything else runs");
                Assert.IsFalse(client.Authenticated);
                Assert.AreEqual(Guid.Empty, client.AgencyId);
                Assert.AreEqual("Unknown", client.PlayerName);
                Assert.AreEqual(0, AgencyStore.Agencies.Count);
                Assert.AreEqual(queued, client.SendMessageQueue.Count, "a rejected client must not get another reply");

                // The very same packet from a client that was not rejected does authenticate.
                var control = f.NewClient();
                f.Receiver.ReceiveCallback(control, f.Packet("Control", AgenciesBuild.Number));
                Assert.IsTrue(control.Authenticated);
                Assert.AreEqual(1, f.Probe.Received.Count(c => ReferenceEquals(c, control)));
            }
        }

        [TestMethod]
        public void SecondRequestWithTheCorrectBuildIsIgnoredWhenHandledDirectly()
        {
            using (var f = new Fixture())
            {
                HandshakeSystem.MismatchDisconnectDelay = () => new TaskCompletionSource<bool>().Task;
                var client = f.NewClient();
                f.Handshake.HandleHandshakeRequest(client, f.Request("Rocketeer", 0));
                Assert.IsTrue(client.HandshakeRejected);
                var queued = client.SendMessageQueue.Count;

                f.Handshake.HandleHandshakeRequest(client, f.Request("Rocketeer", AgenciesBuild.Number));

                Assert.IsFalse(client.Authenticated);
                Assert.AreEqual(Guid.Empty, client.AgencyId);
                Assert.AreEqual("Unknown", client.PlayerName);
                Assert.AreEqual(0, AgencyStore.Agencies.Count);
                Assert.AreEqual(queued, client.SendMessageQueue.Count);
            }
        }

        [TestMethod]
        public void ReceiveCallbackWithANullClientReturnsWithoutThrowing()
        {
            using (var f = new Fixture())
            {
                f.Receiver.ReceiveCallback(null, f.Packet("Late", AgenciesBuild.Number));
                f.Receiver.ReceiveCallback(null, null);
            }
        }

        [TestMethod]
        public void RejectedClientIsDisconnectedAfterTheDelay()
        {
            using (var f = new Fixture())
            {
                var gate = new TaskCompletionSource<bool>();
                HandshakeSystem.MismatchDisconnectDelay = () => gate.Task;
                var client = f.NewClient();

                f.Handshake.HandleHandshakeRequest(client, f.Request("Rocketeer", 0));
                Assert.AreEqual(ConnectionStatus.Connected, client.ConnectionStatus);
                Assert.IsTrue(ServerContext.Clients.ContainsKey(client.Endpoint));

                gate.SetResult(true);
                Assert.IsTrue(SpinWait.SpinUntil(() => client.ConnectionStatus == ConnectionStatus.Disconnected, TimeSpan.FromSeconds(10)));
                Assert.IsFalse(ServerContext.Clients.ContainsKey(client.Endpoint));
            }
        }

        [TestMethod]
        public void DelayedDisconnectLeavesAClientThatWasReplacedOrRemoved()
        {
            using (var f = new Fixture())
            {
                var delay = Task.CompletedTask;
                HandshakeSystem.MismatchDisconnectDelay = () => delay;

                // Same endpoint, different object: the old connection is gone and this one is somebody else's.
                var original = f.NewClient();
                var replacement = f.NewClient();
                ServerContext.Clients[original.Endpoint] = replacement;
                HandshakeSystem.ScheduleMismatchDisconnect(original, "reason").GetAwaiter().GetResult();
                Assert.AreEqual(ConnectionStatus.Connected, original.ConnectionStatus);
                Assert.AreEqual(ConnectionStatus.Connected, replacement.ConnectionStatus);
                Assert.AreSame(replacement, ServerContext.Clients[original.Endpoint]);

                // No longer registered at all.
                var removed = f.NewClient();
                ServerContext.Clients.TryRemove(removed.Endpoint, out _);
                HandshakeSystem.ScheduleMismatchDisconnect(removed, "reason").GetAwaiter().GetResult();
                Assert.AreEqual(ConnectionStatus.Connected, removed.ConnectionStatus);

                // Already disconnected by something else in the meantime.
                var gone = f.NewClient();
                gone.ConnectionStatus = ConnectionStatus.Disconnected;
                HandshakeSystem.ScheduleMismatchDisconnect(gone, "reason").GetAwaiter().GetResult();
                Assert.IsTrue(ServerContext.Clients.ContainsKey(gone.Endpoint), "a client that is not Connected is left alone");

                // The ordinary case still disconnects.
                var live = f.NewClient();
                HandshakeSystem.ScheduleMismatchDisconnect(live, "reason").GetAwaiter().GetResult();
                Assert.AreEqual(ConnectionStatus.Disconnected, live.ConnectionStatus);
            }
        }
    }
}
