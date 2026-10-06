using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
namespace LmpCommonTest
{
 [TestClass]
 public class CommNetOptInPolicyTest
 {
  [TestMethod]
  public void MutualConsentRequiresBothCurrentEndpointStamps()
  {
   var a=new CommNetEndpoint{VesselId=Guid.NewGuid(),OwnerAgencyId=Guid.NewGuid(),OwnershipRevision=2};var b=new CommNetEndpoint{VesselId=Guid.NewGuid(),OwnerAgencyId=Guid.NewGuid(),OwnershipRevision=5};
   var pa=new CommNetPreference{Source=a.Copy(),Targets=new[]{b.Copy()}};var pb=new CommNetPreference{Source=b.Copy(),AcceptAll=true};
   Assert.IsFalse(CommNetOptInPolicy.CanLink(true,true,false,false,a,b,new[]{pa}));
   Assert.IsTrue(CommNetOptInPolicy.CanLink(true,true,false,false,a,b,new[]{pa,pb}));
   b.OwnershipRevision++;Assert.IsFalse(CommNetOptInPolicy.CanLink(true,true,false,false,a,b,new[]{pa,pb}));
   b.OwnershipRevision=5;b.OwnerAgencyId=Guid.NewGuid();Assert.IsFalse(CommNetOptInPolicy.CanLink(true,true,false,false,a,b,new[]{pa,pb}));
  }
  [TestMethod]
  public void HomeSameOwnerOwnerlessAndReadinessRules()
  {
   var a=new CommNetEndpoint{VesselId=Guid.NewGuid(),OwnerAgencyId=Guid.NewGuid()};var b=new CommNetEndpoint{VesselId=Guid.NewGuid(),OwnerAgencyId=a.OwnerAgencyId};
   Assert.IsTrue(CommNetOptInPolicy.CanLink(true,false,true,false,null,null,null));
   Assert.IsTrue(CommNetOptInPolicy.CanLink(true,false,false,false,a,b,null));
   b.OwnerAgencyId=Guid.NewGuid();Assert.IsFalse(CommNetOptInPolicy.CanLink(true,false,false,false,a,b,null));
   Assert.IsFalse(CommNetOptInPolicy.CanLink(true,true,false,false,a,b,null));
   a.OwnerAgencyId=b.OwnerAgencyId=Guid.Empty;Assert.IsFalse(CommNetOptInPolicy.CanLink(true,true,false,false,a,b,null));
   Assert.IsTrue(CommNetOptInPolicy.CanLink(false,false,false,false,null,null,null));
  }
 }
}
