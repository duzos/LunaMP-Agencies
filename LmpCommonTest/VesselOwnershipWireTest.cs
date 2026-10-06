using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace LmpCommonTest
{
 [TestClass]
 public class VesselOwnershipWireTest
 {
  private static NetIncomingMessage Incoming(NetClient peer,NetOutgoingMessage output,int? bits=null)
  {output.Position=0;var input=peer.CreateIncomingMessage(NetIncomingMessageType.Data,output.ReadBytes(output.LengthBytes));input.LengthBits=bits??output.LengthBits;return input;}
  [TestMethod]
  public void RichSnapshotRoundTripLegacyResetAndTruncation()
  {
   var peer=new NetClient(new NetPeerConfiguration("ownership-wire"));var factory=new ServerMessageFactory();
   var source=factory.CreateNewMessageData<AgencyVesselMapSyncMsgData>();source.VesselIds=new[]{Guid.NewGuid()};source.AgencyIds=new[]{Guid.NewGuid()};source.OwnershipSnapshotPresent=true;source.OwnershipRevision=7;
   source.OwnershipRecords=new[]{new VesselOwnershipRecord{VesselId=source.VesselIds[0],OwnerAgencyId=source.AgencyIds[0],Revision=7,CoOwnerAgencyIds=new[]{Guid.NewGuid()},DockingPolicy=VesselDockingPolicy.CoOwners}};
   var output=peer.CreateMessage();source.Serialize(output);var parsed=factory.CreateNewMessageData<AgencyVesselMapSyncMsgData>();parsed.Deserialize(Incoming(peer,output));
   Assert.IsTrue(parsed.OwnershipSnapshotPresent);Assert.AreEqual(7L,parsed.OwnershipRevision);Assert.AreEqual(source.OwnershipRecords[0].CoOwnerAgencyIds[0],parsed.OwnershipRecords[0].CoOwnerAgencyIds[0]);Assert.IsTrue(source.GetMessageSize()>=output.LengthBytes);
   Assert.ThrowsException<EndOfStreamException>(()=>parsed.Deserialize(Incoming(peer,output,output.LengthBits-8)));
   source.OwnershipSnapshotPresent=false;var legacy=peer.CreateMessage();source.Serialize(legacy);parsed.Deserialize(Incoming(peer,legacy));Assert.IsFalse(parsed.OwnershipSnapshotPresent);Assert.AreEqual(0,parsed.OwnershipRecords.Length);
  }
  [TestMethod]
  public void CoupleTailIsBoundedAndPooledLegacyReadsClearState()
  {
   var peer=new NetClient(new NetPeerConfiguration("couple-wire"));var factory=new ClientMessageFactory();var source=factory.CreateNewMessageData<VesselCoupleMsgData>();
   source.OperationId=Guid.NewGuid();source.GrantId=Guid.NewGuid();source.MergedVesselData=new byte[]{1,2,3};var output=peer.CreateMessage();source.Serialize(output);
   var parsed=factory.CreateNewMessageData<VesselCoupleMsgData>();parsed.Deserialize(Incoming(peer,output));Assert.AreEqual(source.OperationId,parsed.OperationId);CollectionAssert.AreEqual(source.MergedVesselData,parsed.MergedVesselData);
   Assert.ThrowsException<EndOfStreamException>(()=>parsed.Deserialize(Incoming(peer,output,output.LengthBits-8)));
   source.OperationId=Guid.Empty;var legacy=peer.CreateMessage();source.Serialize(legacy);parsed.Deserialize(Incoming(peer,legacy));Assert.AreEqual(Guid.Empty,parsed.OperationId);Assert.AreEqual(0,parsed.MergedVesselData.Length);
   source.OperationId=Guid.NewGuid();source.MergedVesselData=new byte[VesselOwnershipPolicy.MaxMergedVesselBytes+1];Assert.ThrowsException<InvalidDataException>(()=>source.Serialize(peer.CreateMessage()));
  }
  [TestMethod]
  public void ConsentAndCommandFactoriesRoundTripExactCorrelation()
  {
   var peer=new NetClient(new NetPeerConfiguration("consent-wire"));var cli=new ClientMessageFactory();var srv=new ServerMessageFactory();
   var command=cli.CreateNewMessageData<AgencyVesselOwnershipCommandMsgData>();command.RequestId=Guid.NewGuid();command.VesselId=Guid.NewGuid();command.TargetAgencyId=Guid.NewGuid();command.Operation=VesselOwnershipOperation.Transfer;
   var output=peer.CreateMessage();command.Serialize(output);var parsed=cli.CreateNewMessageData<AgencyVesselOwnershipCommandMsgData>();parsed.Deserialize(Incoming(peer,output));Assert.AreEqual(command.RequestId,parsed.RequestId);Assert.AreEqual(command.Operation,parsed.Operation);Assert.AreEqual(command.TargetAgencyId,parsed.TargetAgencyId);
   var status=srv.CreateNewMessageData<AgencyDockStatusMsgData>();status.RequestId=command.RequestId;status.GrantId=Guid.NewGuid();status.OperationId=Guid.NewGuid();status.Status=DockConsentStatus.RecoveryRequired;status.Reason="Recovery required";status.ExpiresUtcTicks=DateTime.UtcNow.Ticks;
   output=peer.CreateMessage();status.Serialize(output);var received=srv.CreateNewMessageData<AgencyDockStatusMsgData>();received.Deserialize(Incoming(peer,output));Assert.AreEqual(status.GrantId,received.GrantId);Assert.AreEqual(status.OperationId,received.OperationId);Assert.AreEqual(status.Status,received.Status);Assert.AreEqual(status.ExpiresUtcTicks,received.ExpiresUtcTicks);
  }
 }
}
