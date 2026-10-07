using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace KspControl.Contracts
{
 public sealed class BridgeRequest
 {
  public int ProtocolVersion { get; set; } = 1;
  public string Token { get; set; }
  public string RequestId { get; set; }
  public string Operation { get; set; }
  public string ExpectedWorldEpoch { get; set; }
  public string EntityId { get; set; }
  public long? ExpectedRevision { get; set; }
  public string LeaseId { get; set; }
  public JObject Arguments { get; set; } = new JObject();
 }
 public sealed class BridgeResponse
 {
  public int ProtocolVersion { get; set; } = 1;
  public string RequestId { get; set; }
  public string Status { get; set; }
  public string ReasonCode { get; set; }
  public string WorldEpoch { get; set; }
  public long Revision { get; set; }
  public JObject Data { get; set; } = new JObject();
 }
 public static class BridgeFrames
 {
  public const int MaximumBytes = 1048576;
  public const int DefaultPort = 43819;
  public static void Write(Stream stream, object value)
  {
   var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
   if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException("frame_size");
   int n = bytes.Length;
   stream.Write(new[] {(byte)n,(byte)(n >> 8),(byte)(n >> 16),(byte)(n >> 24)},0,4);
   stream.Write(bytes,0,n); stream.Flush();
  }
  public static T Read<T>(Stream stream)
  {
   var header = ReadExact(stream,4);
   int size = header[0] | header[1]<<8 | header[2]<<16 | header[3]<<24;
   if (size <= 0 || size > MaximumBytes) throw new InvalidDataException("frame_size");
   string text = new UTF8Encoding(false,true).GetString(ReadExact(stream,size));
   using (var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = 32 })
   { return new JsonSerializer { TypeNameHandling = TypeNameHandling.None, CheckAdditionalContent = true }.Deserialize<T>(reader); }
  }
  private static byte[] ReadExact(Stream stream,int size)
  {
   var bytes=new byte[size]; int count=0;
   while(count<size) { int n=stream.Read(bytes,count,size-count); if(n==0) throw new EndOfStreamException(); count+=n; }
   return bytes;
  }
 }
}
