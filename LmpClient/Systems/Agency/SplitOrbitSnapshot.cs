using System;

namespace LmpClient.Systems.Agency
{
    internal static class SplitOrbitSnapshot
    {
        // A decouple callback can run before the new OrbitDriver's first physics update.
        // Reconstruct only that uninitialized snapshot, without advancing the live vessel.
        internal static OrbitSnapshot Replacement(Vessel vessel, ProtoVessel snapshot)
        {
            var original = snapshot.orbitSnapShot;
            if (snapshot.landed || snapshot.splashed || original == null || !IsZero(original)) return null;
            if (!vessel || !vessel.loaded || vessel.packed || !vessel.mainBody || !vessel.rootPart || !vessel.rootPart.rb)
                throw new InvalidOperationException("Split orbit is uninitialized and physical state is unavailable.");

            var body = vessel.mainBody;
            var position = (vessel.CoMD - body.position).xzy;
            var surfaceVelocity = vessel.velocityD.xzy;
            var time = Planetarium.GetUniversalTime();
            if (!Finite(position) || position.sqrMagnitude <= 0 || !Finite(surfaceVelocity) || !Finite(time))
                throw new InvalidOperationException("Split physical state is nonfinite or has no position.");

            var orbit = new Orbit();
            var velocity = surfaceVelocity + orbit.GetRotFrameVelAtPos(body, position);
            if (!Finite(velocity)) throw new InvalidOperationException("Split orbital velocity is nonfinite.");
            orbit.UpdateFromStateVectors(position, velocity, body, time);
            var replacement = new OrbitSnapshot(orbit);
            if (IsZero(replacement) || !Finite(replacement.semiMajorAxis) || !Finite(replacement.eccentricity) ||
                !Finite(replacement.inclination) || !Finite(replacement.argOfPeriapsis) || !Finite(replacement.LAN) ||
                !Finite(replacement.meanAnomalyAtEpoch) || !Finite(replacement.epoch))
                throw new InvalidOperationException("Split orbit reconstruction produced invalid elements.");
            Diagnostics.PlaytestDiagnostics.Write("client.split.orbit-initialized", () => $"vessel={vessel.id} body={replacement.ReferenceBodyIndex} epoch={replacement.epoch} sma={replacement.semiMajorAxis}");
            return replacement;
        }

        private static bool IsZero(OrbitSnapshot orbit) => orbit.semiMajorAxis == 0 && orbit.eccentricity == 0 &&
            orbit.inclination == 0 && orbit.argOfPeriapsis == 0 && orbit.LAN == 0 && orbit.meanAnomalyAtEpoch == 0 && orbit.epoch == 0;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool Finite(Vector3d value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
