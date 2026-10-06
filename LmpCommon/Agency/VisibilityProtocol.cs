using System;
using System.IO;
using System.Linq;
namespace LmpCommon.Agency
{
    public enum VisibilityOperation : byte { SetAgencyShare, SetCraftOverride }
    public sealed class VisibilityEndpoint
    {
        public Guid VesselId, OwnerAgencyId;
        public long OwnershipRevision;
        public VisibilityEndpoint Copy() => new VisibilityEndpoint { VesselId = VesselId, OwnerAgencyId = OwnerAgencyId, OwnershipRevision = OwnershipRevision };
    }
    public sealed class VisibilityAgencyGrant
    {
        public Guid OwnerAgencyId, TargetAgencyId;
    }
    public sealed class VisibilityCraftOverride
    {
        public VisibilityEndpoint Source;
        public Guid TargetAgencyId;
        public VisibilityOverride Rule;
    }
    public sealed class VisibilitySnapshot
    {
        public bool Ready;
        public long Revision;
        public VisibilityEndpoint[] Endpoints = Array.Empty<VisibilityEndpoint>();
        public VisibilityAgencyGrant[] AgencyGrants = Array.Empty<VisibilityAgencyGrant>();
        public VisibilityCraftOverride[] CraftOverrides = Array.Empty<VisibilityCraftOverride>();
    }
    public static class VisibilityLimits
    {
        public const int MaxEndpoints = 10000, MaxAgencyGrants = 4096, MaxCraftOverrides = 10000;
        public static bool SameStamp(VisibilityEndpoint a, VisibilityEndpoint b) => a != null && b != null && a.VesselId == b.VesselId && a.OwnerAgencyId == b.OwnerAgencyId && a.OwnershipRevision == b.OwnershipRevision;
        public static void Validate(VisibilitySnapshot value)
        {
            if (value == null || value.Revision < 0 || value.Endpoints == null || value.AgencyGrants == null || value.CraftOverrides == null || value.Endpoints.Length > MaxEndpoints || value.AgencyGrants.Length > MaxAgencyGrants || value.CraftOverrides.Length > MaxCraftOverrides) throw new InvalidDataException("Visibility snapshot exceeds limits.");
            foreach (var endpoint in value.Endpoints) ValidateEndpoint(endpoint);
            if (value.Endpoints.Select(e => e.VesselId).Distinct().Count() != value.Endpoints.Length) throw new InvalidDataException("Duplicate visibility endpoint.");
            foreach (var grant in value.AgencyGrants)
                if (grant == null || grant.OwnerAgencyId == Guid.Empty || grant.TargetAgencyId == Guid.Empty || grant.OwnerAgencyId == grant.TargetAgencyId) throw new InvalidDataException("Invalid agency visibility grant.");
            if (value.AgencyGrants.Select(g => Tuple.Create(g.OwnerAgencyId, g.TargetAgencyId)).Distinct().Count() != value.AgencyGrants.Length) throw new InvalidDataException("Duplicate agency visibility grant.");
            foreach (var rule in value.CraftOverrides)
            {
                if (rule == null || rule.TargetAgencyId == Guid.Empty || rule.Rule == VisibilityOverride.Inherit || !Enum.IsDefined(typeof(VisibilityOverride), rule.Rule)) throw new InvalidDataException("Invalid craft visibility override.");
                ValidateEndpoint(rule.Source);
                if (rule.Source.OwnerAgencyId == Guid.Empty || rule.Source.OwnerAgencyId == rule.TargetAgencyId) throw new InvalidDataException("Invalid visibility owner.");
            }
            if (value.CraftOverrides.Select(r => Tuple.Create(r.Source.VesselId, r.TargetAgencyId)).Distinct().Count() != value.CraftOverrides.Length) throw new InvalidDataException("Duplicate craft visibility override.");
        }
        private static void ValidateEndpoint(VisibilityEndpoint endpoint)
        {
            if (endpoint == null || endpoint.VesselId == Guid.Empty || endpoint.OwnershipRevision < 0) throw new InvalidDataException("Invalid visibility endpoint.");
        }
    }
}
