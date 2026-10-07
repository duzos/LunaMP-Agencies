using System.Globalization;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] public class JournalAccessTests
{
 private string directory=null!;
 [TestInitialize] public void Init()=>directory=Path.Combine(Path.GetTempPath(),"ksp-control-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ if(Directory.Exists(directory)) Directory.Delete(directory,true); }

 [TestMethod] public void TheMirroredGrantExpiryIsReadExactlyUnderADayFirstCulture()
 {
  // JObject.Parse turns "expiresUtc" into a Date token; a culture round trip through a string read 8 Oct as 10 Aug and expired the grant.
  var expires=new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(9),TimeSpan.Zero);
  var saved=CultureInfo.CurrentCulture;
  try
  {
   CultureInfo.CurrentCulture=new CultureInfo("en-GB");
   var status=JObject.Parse("{\"grant\":{\"operations\":[\"editor.replace_craft\"],\"facilities\":[\"VAB\"],\"expiresUtc\":\""+expires.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",CultureInfo.InvariantCulture)+"\",\"spendLimitFunds\":0}}");
   var data=new JObject { ["leaseId"]="0123456789abcdef0123456789abcdef",["grantId"]="grant",["generation"]=5,["epoch"]="world",["expiresInSeconds"]=300,["purpose"]="test" };
   using var access=new JournalAccess(directory);
   access.MirrorAcquire(data,status);
   Assert.AreEqual(expires,access.CurrentGrant!.ExpiresAt);
   access.TryGet()!.Admit("req","grant",5,"0123456789abcdef0123456789abcdef","world","editor.replace_craft","editor:VAB","{}",0,DateTimeOffset.UtcNow);
  }
  finally { CultureInfo.CurrentCulture=saved; }
 }
}
