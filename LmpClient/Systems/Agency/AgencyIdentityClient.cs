using System;
using System.Collections.Generic;
using System.Threading;
using LmpCommon.Agency;
using LmpClient.Systems.PlayerColorSys;
using UnityEngine;

namespace LmpClient.Systems.Agency
{
    internal static class AgencyIdentityClient
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<Guid, AgencyIdentityInfo> Identities = new Dictionary<Guid, AgencyIdentityInfo>();
        private static readonly HashSet<Guid> Deleted = new HashSet<Guid>();
        private static int supported, refresh, version;
        // Bumped on every identity change so main-thread presentation caches (AgencyPresentation) know to rebuild.
        internal static int Version => Volatile.Read(ref version);
        internal static bool Supported => Volatile.Read(ref supported) == 1;
        internal static void BeginSession()
        {
            lock (Gate) { Identities.Clear(); Deleted.Clear(); Volatile.Write(ref supported, -1); }
            RequestRefresh();
        }
        internal static void Negotiate(int version)
        {
            lock (Gate)
            {
                Volatile.Write(ref supported, version == 1 ? 1 : 0);
                if (version != 1) { Identities.Clear(); Deleted.Clear(); }
            }
            RequestRefresh();
        }
        internal static AgencyIdentityInfo Get(Guid id)
        {
            lock (Gate) return Supported && Identities.TryGetValue(id, out var value) ? Copy(value) :
                new AgencyIdentityInfo { AgencyId = id, FlagUrl = AgencyIdentityDefaults.DefaultFlagUrl };
        }
        private static AgencyIdentityInfo Copy(AgencyIdentityInfo v) => new AgencyIdentityInfo {
            AgencyId = v.AgencyId, Revision = v.Revision, HasColour = v.HasColour,
            Red = v.Red, Green = v.Green, Blue = v.Blue, FlagUrl = v.FlagUrl };
        internal static void Receive(AgencyIdentityInfo value)
        {
            if (value == null || value.AgencyId == Guid.Empty) return;
            lock (Gate)
            {
                if (Volatile.Read(ref supported) == 0 || Deleted.Contains(value.AgencyId) || (Identities.TryGetValue(value.AgencyId, out var old) && old.Revision >= value.Revision)) return;
                if (!Identities.ContainsKey(value.AgencyId) && Identities.Count >= AgencyIdentityDefaults.MaxIdentities) return;
                Identities[value.AgencyId] = Copy(value);
            }
            RequestRefresh();
        }
        internal static void Remove(Guid id) { lock (Gate) { Deleted.Add(id); Identities.Remove(id); } RequestRefresh(); }
        internal static void Clear()
        {
            lock (Gate) { Volatile.Write(ref supported, 0); Identities.Clear(); Deleted.Clear(); }
            RequestRefresh();
            AgencyPresentation.Clear();
        }
        internal static bool TryColour(Guid id, out Color colour)
        {
            var value = Get(id);
            colour = new Color32(value.Red, value.Green, value.Blue, 255);
            return value.HasColour;
        }
        internal static void RequestRefresh()
        {
            Interlocked.Increment(ref version);
            Interlocked.Exchange(ref refresh, 1);
        }
        // Called by the agency system's Unity Update routine, never the network handler.
        internal static void Update()
        {
            if (Interlocked.Exchange(ref refresh, 0) == 0) return;
            if (FlightGlobals.Vessels == null) return;
            foreach (var vessel in FlightGlobals.Vessels)
                if (vessel) PlayerColorSystem.Singleton.SetVesselOrbitColor(vessel);
        }
    }
}
