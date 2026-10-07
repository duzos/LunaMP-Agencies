namespace LmpCommon.Agency
{
    /// <summary>
    /// The fork build this binary belongs to. Bump <see cref="Number"/> for every release; everything else is derived from it.
    /// Scripts/Package-AgenciesRelease.ps1 reads <see cref="Number"/> and <see cref="UpstreamVersion"/> from this file by regex,
    /// so keep each of them as a single-line const declaration.
    /// </summary>
    public static class AgenciesBuild
    {
        public const int Number = 5;
        public const string UpstreamVersion = "0.30.0";
        public const string Owner = "duzos";
        public const string Repo = "LunaMP-Agencies";

        /// <summary>Release tag of this build, for example v0.30.0-agencies.2.</summary>
        public static readonly string Tag = "v" + UpstreamVersion + "-agencies." + Number;

        public const string LatestReleaseApi = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";
        public const string ReleasesPage = "https://github.com/" + Owner + "/" + Repo + "/releases/latest";
        /// <summary>The only place update assets may be downloaded from: this + tag + "/" + asset name.</summary>
        public const string DownloadPrefix = "https://github.com/" + Owner + "/" + Repo + "/releases/download/";

        public const string ClientAssetPrefix = "LunaMultiplayer-Agencies-Client-";
        public const string ServerAssetPrefix = "LunaMultiplayer-Agencies-Server-";
        public const string SumsAsset = "SHA256SUMS.txt";

        public static string AssetName(string prefix, string tag) => prefix + tag + ".zip";
    }
}
