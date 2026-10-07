using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LmpCommon.Agency;
using LmpClient.Systems.SettingsSys;
using UnityEngine;

namespace LmpClient.Systems.Agency
{
    internal sealed class ContactSnapshot
    {
        internal CelestialBody Body;
        internal Vector3d Position;
        internal Vector3d[] OrbitPoints;
        internal string AgencyName, Size, VesselName;
        internal ContactSnapshot Copy() => new ContactSnapshot { Body = Body, Position = Position,
            OrbitPoints = OrbitPoints == null ? null : (Vector3d[])OrbitPoints.Clone(), AgencyName = AgencyName, Size = Size, VesselName = VesselName };
    }
    internal static class VisibilityContacts
    {
        private static readonly object Gate = new object();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static VisibilityContactTracker<ContactSnapshot> tracker;
        private static readonly Dictionary<Guid,double> samples = new Dictionary<Guid,double>();
        private sealed class Captured { internal Guid Owner; internal long Revision; internal double Time; internal ContactSnapshot Snapshot; }
        private static readonly Dictionary<Guid,Captured> captures = new Dictionary<Guid,Captured>();
        private static GameScenes scene;
        private static Guid viewer;
        private static double classify, expiry;
        internal static double Now => Clock.Elapsed.TotalSeconds;
        internal static void Clear() { lock (Gate) { tracker = null; samples.Clear(); captures.Clear(); viewer = Guid.Empty; } }
        internal static void Prepare()
        {
            lock (Gate)
            {
                var cutoff = Now - VisibilityContactTracker<ContactSnapshot>.FreshnessSeconds;
                foreach (var id in samples.Where(entry => entry.Value < cutoff).Select(entry => entry.Key).ToArray()) { samples.Remove(id); captures.Remove(id); }
                var settings = SettingsSystem.ServerSettings;
                var currentViewer = AgencySystem.Singleton.MyAgencyId;
                var classification = settings.AgencyContactClassificationSeconds;
                var expiration = settings.AgencyContactExpirySeconds;
                if (tracker == null || viewer != currentViewer || classify != classification || expiry != expiration)
                {
                    tracker = new VisibilityContactTracker<ContactSnapshot>(s => s.Copy(), classification, expiration);
                    samples.Clear(); captures.Clear(); viewer = currentViewer; classify = classification; expiry = expiration;
                }
                if (scene != HighLogic.LoadedScene) { tracker.LoseAll(Now); samples.Clear(); captures.Clear(); scene = HighLogic.LoadedScene; }
            }
        }
        internal static void Reconcile(Func<Guid,Guid,long,bool> valid)
        {
            lock (Gate)
            {
                tracker?.ReconcileOwnership(valid);
                foreach(var id in captures.Where(entry => !valid(entry.Key,entry.Value.Owner,entry.Value.Revision)).Select(entry => entry.Key).ToArray())
                { captures.Remove(id); samples.Remove(id); }
            }
        }
        internal static bool Identified(Guid id)
        { lock (Gate) return tracker != null && tracker.IsIdentifiedAndDetected(id, Now); }
        internal static void Observe(Vessel vessel, Guid owner, long revision, bool detected, bool close, bool bypass)
        {
            lock (Gate)
            {
                if (tracker == null) return;
                var now = Now;
                if (bypass)
                {
                    tracker.Forget(vessel.id);
                    samples.Remove(vessel.id); captures.Remove(vessel.id); return;
                }
                if (!detected) { tracker.Lose(vessel.id, now); samples.Remove(vessel.id); captures.Remove(vessel.id); return; }
                var elapsed = samples.TryGetValue(vessel.id, out var previous) && now - previous <= 1 ? now - previous : 0;
                samples[vessel.id] = now;
                ContactSnapshot snapshot;
                if(captures.TryGetValue(vessel.id,out var captured) && captured.Owner == owner && captured.Revision == revision && now-captured.Time < .5)
                    snapshot=captured.Snapshot;
                else
                {
                    snapshot=Capture(vessel,owner);
                    if(snapshot!=null)
                    {
                        if(captures.Count>=10000 && !captures.ContainsKey(vessel.id)) captures.Remove(captures.OrderBy(entry=>entry.Value.Time).First().Key);
                        captures[vessel.id]=new Captured{Owner=owner,Revision=revision,Time=now,Snapshot=snapshot};
                    }
                }
                if (snapshot == null) { tracker.Lose(vessel.id, now); samples.Remove(vessel.id); captures.Remove(vessel.id); return; }
                tracker.Observe(vessel.id, owner, revision, snapshot, now, elapsed, close);
            }
        }
        internal static IReadOnlyList<VisibilityContactView<ContactSnapshot>> Views()
        { lock (Gate) return tracker == null ? Array.Empty<VisibilityContactView<ContactSnapshot>>() : tracker.GetViews(Now, 128); }
        internal static double ClassificationSeconds => classify;
        internal static double ExpirySeconds => expiry;
        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static Vector3d Rough(Vector3d p) => new Vector3d(Math.Round(p.x / 10000) * 10000, Math.Round(p.y / 10000) * 10000, Math.Round(p.z / 10000) * 10000);
        private static ContactSnapshot Capture(Vessel vessel, Guid owner)
        {
            if (!vessel || !vessel.mainBody) return null;
            var relative = vessel.GetWorldPos3D() - vessel.mainBody.position;
            if (!Finite(relative.x) || !Finite(relative.y) || !Finite(relative.z)) return null;
            AgencySystem.Singleton.KnownAgencies.TryGetValue(owner, out var agency);
            var parts = vessel.protoVessel?.protoPartSnapshots?.Count ?? vessel.parts?.Count ?? 0;
            var snapshot = new ContactSnapshot { Body = vessel.mainBody, Position = Rough(relative),
                AgencyName = agency?.Name ?? "Unknown agency", Size = parts < 10 ? "Small" : parts < 40 ? "Medium" : "Large",
                VesselName = vessel.vesselName ?? "Unknown craft", OrbitPoints = Array.Empty<Vector3d>() };
            var source = vessel.orbit;
            if (vessel.LandedOrSplashed || source == null || source.referenceBody != vessel.mainBody) return snapshot;
            try
            {
                var orbit = new Orbit(source.inclination, source.eccentricity, source.semiMajorAxis, source.LAN,
                    source.argumentOfPeriapsis, source.meanAnomalyAtEpoch, source.epoch, source.referenceBody);
                var start = Planetarium.GetUniversalTime();
                var duration = source.eccentricity < 1 && Finite(source.period) ? Math.Min(source.period, 86400 * 30) : 3600;
                if (!Finite(duration) || duration <= 0) return snapshot;
                var points = new Vector3d[49];
                for (var i = 0; i < points.Length; i++)
                {
                    var p = orbit.getPositionAtUT(start + duration * i / 48) - vessel.mainBody.position;
                    if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z)) return snapshot;
                    points[i] = Rough(p);
                }
                snapshot.OrbitPoints = points;
            }
            catch { /* Invalid orbit retains only the captured rough marker. */ }
            return snapshot;
        }
    }
}
