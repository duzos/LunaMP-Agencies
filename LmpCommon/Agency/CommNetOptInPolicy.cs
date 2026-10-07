using System;
using System.Collections.Generic;
using System.Linq;
namespace LmpCommon.Agency
{
    public enum CommNetOperation : byte { SetAcceptAll, SetTarget, SetActiveScanning }
    public sealed class CommNetEndpoint
    {
        public Guid VesselId;
        public Guid OwnerAgencyId;
        public long OwnershipRevision;
        public CommNetEndpoint Copy() => new CommNetEndpoint { VesselId=VesselId, OwnerAgencyId=OwnerAgencyId, OwnershipRevision=OwnershipRevision };
    }
    public sealed class CommNetPreference
    {
        public CommNetEndpoint Source;
        public bool AcceptAll;
        public bool ActiveScanning;
        public CommNetEndpoint[] Targets = Array.Empty<CommNetEndpoint>();
        public CommNetPreference Copy() => new CommNetPreference { Source=Source.Copy(), AcceptAll=AcceptAll, ActiveScanning=ActiveScanning, Targets=Targets.Select(t=>t.Copy()).ToArray() };
    }
    public static class CommNetOptInPolicy
    {
        public const int MaxEndpoints=10000;
        public const int MaxPreferences=10000;
        public const int MaxTargets=128;
        public const int MaxTotalTargets=32768;
        public static bool SameStamp(CommNetEndpoint a,CommNetEndpoint b) => a!=null && b!=null && a.VesselId==b.VesselId && a.OwnerAgencyId==b.OwnerAgencyId && a.OwnershipRevision==b.OwnershipRevision;
        public static bool CanLink(bool enabled,bool ready,bool homeA,bool homeB,CommNetEndpoint a,CommNetEndpoint b,IEnumerable<CommNetPreference> preferences)
        {
            if(!enabled || homeA || homeB) return true;
            if(a==null || b==null || a.OwnerAgencyId==Guid.Empty || b.OwnerAgencyId==Guid.Empty) return false;
            if(a.OwnerAgencyId==b.OwnerAgencyId) return true;
            if(!ready || preferences==null) return false;
            var rows=preferences as CommNetPreference[] ?? preferences.ToArray();
            return Consents(a,b,rows) && Consents(b,a,rows);
        }
        private static bool Consents(CommNetEndpoint source,CommNetEndpoint target,IEnumerable<CommNetPreference> preferences)
            => preferences.Any(p=>p!=null && SameStamp(p.Source,source) && (p.AcceptAll || (p.Targets!=null && p.Targets.Any(t=>SameStamp(t,target)))));
    }
}
