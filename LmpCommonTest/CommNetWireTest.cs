using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
namespace LmpCommonTest
{
 [TestClass]
 public class CommNetWireTest
 {
  private static NetIncomingMessage Incoming(NetClient peer,NetOutgoingMessage m,int? bits=null){m.Position=0;var input=peer.CreateIncomingMessage(NetIncomingMessageType.Data,m.ReadBytes(m.LengthBytes));input.LengthBits=bits??m.LengthBits;return input;}
  [TestMethod]
  public void SnapshotRoundtripAndTruncatedPooledReadFailsClosed()
  {
   var peer=new NetClient(new NetPeerConfiguration("commnet-wire"));var factory=new ServerMessageFactory();
   var a=new CommNetEndpoint{VesselId=Guid.NewGuid(),OwnerAgencyId=Guid.NewGuid(),OwnershipRevision=3};var b=new CommNetEndpoint{VesselId=Guid.NewGuid(),OwnerAgencyId=Guid.NewGuid(),OwnershipRevision=4};
   var source=factory.CreateNewMessageData<AgencyCommNetSnapshotMsgData>();source.Ready=true;source.Revision=8;source.Endpoints=new[]{a,b};source.Preferences=new[]{new CommNetPreference{Source=a,Targets=new[]{b}},new CommNetPreference{Source=b,AcceptAll=true}};
   var output=peer.CreateMessage();source.Serialize(output);Assert.IsTrue(source.GetMessageSize()>=output.LengthBytes);var target=factory.CreateNewMessageData<AgencyCommNetSnapshotMsgData>();target.Deserialize(Incoming(peer,output));
   Assert.IsTrue(target.Ready);Assert.AreEqual(8L,target.Revision);Assert.AreEqual(4L,target.Preferences[0].Targets[0].OwnershipRevision);Assert.IsTrue(target.Preferences[1].AcceptAll);
   Assert.ThrowsException<EndOfStreamException>(()=>target.Deserialize(Incoming(peer,output,output.LengthBits-1)));Assert.IsFalse(target.Ready);Assert.AreEqual(0,target.Endpoints.Length);
  }
  [TestMethod]
  public void CommandAndResultCorrelationRoundtrip()
  {
   var peer=new NetClient(new NetPeerConfiguration("commnet-command"));var cli=new ClientMessageFactory();var srv=new ServerMessageFactory();
   var command=cli.CreateNewMessageData<AgencyCommNetCommandMsgData>();command.RequestId=Guid.NewGuid();command.VesselId=Guid.NewGuid();command.TargetVesselId=Guid.NewGuid();command.Operation=CommNetOperation.SetTarget;command.Enabled=true;
   var output=peer.CreateMessage();command.Serialize(output);var read=cli.CreateNewMessageData<AgencyCommNetCommandMsgData>();read.Deserialize(Incoming(peer,output));Assert.AreEqual(command.RequestId,read.RequestId);Assert.AreEqual(command.TargetVesselId,read.TargetVesselId);Assert.IsTrue(read.Enabled);Assert.AreEqual(command.Operation,read.Operation);
   var result=srv.CreateNewMessageData<AgencyCommNetResultMsgData>();result.RequestId=command.RequestId;result.Success=false;result.Reason="Not your craft";output=peer.CreateMessage();result.Serialize(output);var reply=srv.CreateNewMessageData<AgencyCommNetResultMsgData>();reply.Deserialize(Incoming(peer,output));Assert.AreEqual(command.RequestId,reply.RequestId);Assert.IsFalse(reply.Success);Assert.AreEqual(result.Reason,reply.Reason);
  }
  [TestMethod]
  public void OversizedPreferenceArrayRejectedBeforeWriting()
  {
   var peer=new NetClient(new NetPeerConfiguration("commnet-bounds"));var factory=new ServerMessageFactory();var m=factory.CreateNewMessageData<AgencyCommNetSnapshotMsgData>();m.Preferences=new CommNetPreference[CommNetOptInPolicy.MaxPreferences+1];
   Assert.ThrowsException<InvalidDataException>(()=>m.Serialize(peer.CreateMessage()));
  }
 }
}
