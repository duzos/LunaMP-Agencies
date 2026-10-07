using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.HostTests;
[TestClass] public class ProtocolTests
{
 [TestMethod] public void RoundtripPreservesRequest() { using var stream=new MemoryStream(); BridgeFrames.Write(stream,new BridgeRequest { RequestId="one",Operation="game.context",ExpectedRevision=42 }); stream.Position=0; var read=BridgeFrames.Read<BridgeRequest>(stream); Assert.AreEqual("one",read.RequestId); Assert.AreEqual(42L,read.ExpectedRevision); }
 [DataTestMethod][DataRow(0)][DataRow(-1)][DataRow(1048577)] public void InvalidLengthsRejected(int size) { using var stream=new MemoryStream(BitConverter.GetBytes(size)); Assert.ThrowsException<InvalidDataException>(()=>BridgeFrames.Read<BridgeRequest>(stream)); }
 [TestMethod] public void TruncatedFrameRejected() { using var stream=new MemoryStream(new byte[]{3,0,0,0,123}); Assert.ThrowsException<EndOfStreamException>(()=>BridgeFrames.Read<BridgeRequest>(stream)); }
}
