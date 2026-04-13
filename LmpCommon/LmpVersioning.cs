using System;
using System.Reflection;

namespace LmpCommon
{
    public class LmpVersioning
    {
        private static Version AssemblyVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version;

        public static ushort MajorVersion { get; } = (ushort)AssemblyVersion.Major;
        public static ushort MinorVersion { get; } = (ushort)AssemblyVersion.Minor;
        public static ushort BuildVersion { get; } = (ushort)AssemblyVersion.Build;

        public static Version CurrentVersion { get; } = new Version(AssemblyVersion.ToString(3));

        /// <summary>
        /// Additional major/minor pairs that may interoperate (both directions). Build/revision is ignored.
        /// Add a row when releasing a version that should still exchange messages with the previous minor line.
        /// </summary>
        private static readonly (int MajorA, int MinorA, int MajorB, int MinorB)[] CrossCompatibleVersionLines =
        {
            (0, 30, 0, 29), //  Version 0.30.x is compatible with version 0.29.x
        };

        /// <summary>
        /// Returns true if the peer version is compatible with this assembly (same major+minor, or listed cross-compatible pair).
        /// </summary>
        public static bool IsCompatible(Version version)
        {
            return IsCompatibleWithPeer(MajorVersion, MinorVersion, version);
        }

        /// <summary>
        /// Returns true if the version passed as parameter is compatible with your current version
        /// </summary>
        public static bool IsCompatible(string versionStr)
        {
            try
            {
                return IsCompatible(new Version(versionStr));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true if the version passed as parameter is compatible with your current version
        /// </summary>
        public static bool IsCompatible(int major, int minor, int build)
        {
            return IsCompatibleWithPeer(MajorVersion, MinorVersion, new Version(major, minor, build));
        }

        private static bool IsCompatibleWithPeer(int localMajor, int localMinor, Version peerVersion)
        {
            if (peerVersion.Major == localMajor && peerVersion.Minor == localMinor)
                return true;

            var peerMajor = peerVersion.Major;
            var peerMinor = peerVersion.Minor;

            foreach (var (majorA, minorA, majorB, minorB) in CrossCompatibleVersionLines)
            {
                if (localMajor == majorA && localMinor == minorA && peerMajor == majorB && peerMinor == minorB)
                    return true;
                if (localMajor == majorB && localMinor == minorB && peerMajor == majorA && peerMinor == minorA)
                    return true;
            }

            return false;
        }
    }
}
