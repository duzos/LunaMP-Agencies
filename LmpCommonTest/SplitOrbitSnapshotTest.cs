using System;
using LmpClient.Systems.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// Physics doubles verify the production capture policy and coordinate inputs;
// they do not simulate KSP's orbital mechanics or replace a live collision test.
public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double a, double b, double c) { x = a; y = b; z = c; }
    public Vector3d xzy => new Vector3d(x, z, y);
    public double sqrMagnitude => x*x + y*y + z*z;
    public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.x+b.x,a.y+b.y,a.z+b.z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x-b.x,a.y-b.y,a.z-b.z);
}
public class CelestialBody : TestUnityObject { public Vector3d position; }
public static class Planetarium { public static double Time; public static double GetUniversalTime() => Time; }
public class Orbit
{
    public static Vector3d Position, Velocity, Rotation;
    public static int Calls;
    public static double ResultSma;
    public double Epoch;
    public Vector3d GetRotFrameVelAtPos(CelestialBody body, Vector3d position) => Rotation;
    public void UpdateFromStateVectors(Vector3d position, Vector3d velocity, CelestialBody body, double time)
    { Position = position; Velocity = velocity; Epoch = time; Calls++; }
}
public class OrbitSnapshot
{
    public double semiMajorAxis, eccentricity, inclination, argOfPeriapsis, LAN, meanAnomalyAtEpoch, epoch;
    public int ReferenceBodyIndex;
    public OrbitSnapshot() { }
    public OrbitSnapshot(Orbit orbit) { semiMajorAxis = Orbit.ResultSma; epoch = orbit.Epoch; }
}

namespace LmpCommonTest
{
    [TestClass]
    public class SplitOrbitSnapshotTest
    {
        private Vessel physical;
        private ProtoVessel snapshot;
        [TestInitialize] public void Setup()
        {
            Orbit.Calls = 0; Orbit.ResultSma = 600000; Orbit.Rotation = new Vector3d(1,2,3); Planetarium.Time = 123;
            physical = new Vessel { loaded = true, mainBody = new CelestialBody { position = new Vector3d(10,20,30) },
                CoMD = new Vector3d(110,220,330), velocityD = new Vector3d(4,5,6), rootPart = new Part { rb = new TestUnityObject() } };
            snapshot = new ProtoVessel { orbitSnapShot = new OrbitSnapshot() };
            snapshot.protoPartSnapshots.Add(new ProtoPartSnapshot { flightID = 42 });
        }
        [TestMethod] public void ZeroOrbitUsesCurrentPhysicsWithoutChangingOriginalSnapshot()
        {
            var original = snapshot.orbitSnapShot;
            var repaired = SplitOrbitSnapshot.Replacement(physical,snapshot);
            Assert.AreEqual(100d,Orbit.Position.x); Assert.AreEqual(300d,Orbit.Position.y); Assert.AreEqual(200d,Orbit.Position.z);
            Assert.AreEqual(5d,Orbit.Velocity.x); Assert.AreEqual(8d,Orbit.Velocity.y); Assert.AreEqual(8d,Orbit.Velocity.z);
            Assert.AreEqual(123d,repaired.epoch); Assert.AreSame(original,snapshot.orbitSnapShot);
            Assert.AreEqual(42u,snapshot.protoPartSnapshots[0].flightID);
        }
        [TestMethod] public void ExistingOrbitAndSurfaceSnapshotsRemainUnchanged()
        {
            snapshot.orbitSnapShot.semiMajorAxis=12;
            Assert.IsNull(SplitOrbitSnapshot.Replacement(null,snapshot));
            snapshot.orbitSnapShot.semiMajorAxis=0; snapshot.landed=true;
            Assert.IsNull(SplitOrbitSnapshot.Replacement(null,snapshot));
            snapshot.landed=false; snapshot.splashed=true;
            Assert.IsNull(SplitOrbitSnapshot.Replacement(null,snapshot)); Assert.AreEqual(0,Orbit.Calls);
        }
        [TestMethod] public void MissingOrPackedPhysicsCannotInventOrbit()
        {
            physical.packed=true;
            Assert.ThrowsException<InvalidOperationException>(()=>SplitOrbitSnapshot.Replacement(physical,snapshot));
            physical.packed=false; physical.rootPart.rb=null;
            Assert.ThrowsException<InvalidOperationException>(()=>SplitOrbitSnapshot.Replacement(physical,snapshot));
            Assert.AreEqual(0,Orbit.Calls);
        }
        [TestMethod] public void NonfiniteInputAndDerivedElementsAreRejected()
        {
            physical.velocityD=new Vector3d(double.NaN,0,0);
            Assert.ThrowsException<InvalidOperationException>(()=>SplitOrbitSnapshot.Replacement(physical,snapshot));
            Assert.AreEqual(0,Orbit.Calls);
            physical.velocityD=new Vector3d(); Orbit.ResultSma=double.PositiveInfinity;
            Assert.ThrowsException<InvalidOperationException>(()=>SplitOrbitSnapshot.Replacement(physical,snapshot));
            Assert.AreEqual(0d,snapshot.orbitSnapShot.semiMajorAxis);
        }
    }
}
