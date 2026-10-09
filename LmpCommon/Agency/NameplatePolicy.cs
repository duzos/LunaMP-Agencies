using System;

namespace LmpCommon.Agency
{
    /// <summary>
    /// Vessel kinds for <see cref="NameplatePolicy"/>. Values match KSP's <c>VesselType</c> so the client
    /// casts directly; NameplatePolicyTest pins the values.
    /// </summary>
    public enum VesselTypeCode
    {
        Debris = 0,
        SpaceObject = 1,
        Unknown = 2,
        Probe = 3,
        Relay = 4,
        Rover = 5,
        Lander = 6,
        Ship = 7,
        Plane = 8,
        Station = 9,
        Base = 10,
        Eva = 11,
        Flag = 12,
        DeployedScienceController = 13,
        DeployedSciencePart = 14,
        DroppedPart = 15,
        DeployedGroundPart = 16,
    }

    /// <summary>
    /// Pure rules for flight nameplates over rival craft (plan 41 S3). No Unity types.
    /// </summary>
    public static class NameplatePolicy
    {
        /// <summary>Most plates drawn at once; the nearest win.</summary>
        public const int ClampCount = 16;
        /// <summary>Plates are fully opaque up to this fraction of the range, then fade linearly.</summary>
        public const double FadeStartFraction = 0.7;
        /// <summary>Alpha at the edge of the range.</summary>
        public const float MinAlpha = 0.25f;

        /// <summary>
        /// Everything except visibility: a rival owner (not empty, not mine), not the active vessel, a craft
        /// type worth labelling and within <paramref name="maxKm"/>. Cheap, so callers test it before the
        /// locking visibility check.
        /// </summary>
        public static bool IsCandidate(bool featureOn, Guid owner, Guid mine, bool isActiveVessel, VesselTypeCode type, double distSq, double maxKm)
        {
            if (!featureOn || isActiveVessel) return false;
            if (owner == Guid.Empty || owner == mine) return false;
            if (!IsLabelledType(type)) return false;
            if (double.IsNaN(distSq) || double.IsNaN(maxKm) || maxKm <= 0) return false;
            var maxMetres = maxKm * 1000d;
            return distSq <= maxMetres * maxMetres;
        }

        /// <summary><see cref="IsCandidate"/> plus full visibility: classified craft never get a plate.</summary>
        public static bool ShouldShow(bool featureOn, Guid owner, Guid mine, bool isActiveVessel, bool canSee, VesselTypeCode type, double distSq, double maxKm) =>
            canSee && IsCandidate(featureOn, owner, mine, isActiveVessel, type, distSq, maxKm);

        /// <summary>
        /// Crewed or controllable craft and kerbals on EVA. Debris, flags, asteroids/comets, unknown objects
        /// and loose or deployed ground parts are noise.
        /// </summary>
        public static bool IsLabelledType(VesselTypeCode type)
        {
            switch (type)
            {
                case VesselTypeCode.Probe:
                case VesselTypeCode.Relay:
                case VesselTypeCode.Rover:
                case VesselTypeCode.Lander:
                case VesselTypeCode.Ship:
                case VesselTypeCode.Plane:
                case VesselTypeCode.Station:
                case VesselTypeCode.Base:
                case VesselTypeCode.Eva:
                case VesselTypeCode.DeployedScienceController:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>1 up to 70% of <paramref name="max"/>, then a linear fade to <see cref="MinAlpha"/> at the edge (and beyond).</summary>
        public static float Alpha(double dist, double max)
        {
            if (double.IsNaN(dist) || double.IsNaN(max) || max <= 0) return MinAlpha;
            var start = max * FadeStartFraction;
            if (dist <= start) return 1f;
            if (dist >= max) return MinAlpha;
            var t = (dist - start) / (max - start);
            return (float)(1d - t * (1d - MinAlpha));
        }

        /// <summary>
        /// Inserts (<paramref name="key"/>, <paramref name="distSq"/>) into the first <paramref name="count"/>
        /// entries of <paramref name="distances"/>, kept ascending, capped at the array length. Returns the
        /// slot written, or -1 when the array is full and the entry is farther than all of them. The caller
        /// shifts its own parallel arrays with <see cref="ShiftRight{T}"/> using the returned slot.
        /// </summary>
        public static int InsertSlot(double[] distances, int count, double distSq)
        {
            var capacity = distances.Length;
            if (count >= capacity && distSq >= distances[capacity - 1]) return -1;
            var slot = Math.Min(count, capacity - 1);
            while (slot > 0 && distances[slot - 1] > distSq) slot--;
            return slot;
        }

        /// <summary>Moves entries [slot, last) one place right, dropping the last when full, so slot can be overwritten.</summary>
        public static void ShiftRight<T>(T[] items, int count, int slot)
        {
            var last = Math.Min(count, items.Length - 1);
            for (var i = last; i > slot; i--) items[i] = items[i - 1];
        }
    }
}
