using System.Globalization;
using System.IO;

namespace LmpCommon.Agency
{
    /// <summary>
    /// Update working folders below a root chosen by the caller (client: KSP/LunaMultiplayer-update, server: binaries/update-staging).
    /// Only callers use these; the helper process receives the resulting paths explicitly.
    /// </summary>
    public static class AgenciesUpdatePaths
    {
        public static string StagingDir(string root, int build) => Path.Combine(root, build.ToString(CultureInfo.InvariantCulture));
        public static string ExtractDir(string root, int build) => Path.Combine(StagingDir(root, build), "extract");
        public static string BackupDir(string root) => Path.Combine(root, "backup");
        public static string ResultPath(string root) => Path.Combine(root, "last-result.txt");
    }
}
