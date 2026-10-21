using System;
using System.Globalization;

namespace PCLDownloadFix
{
    /// <summary>
    /// The build identifiers PCL embeds as constants in PCL.ModBase:
    /// VersionCode (411, 412, 413, 414, ...) and BuildType (0 = snapshot,
    /// 50 = release). They are rendered as e.g. 411.50 or 414.00.
    /// </summary>
    public sealed class PclBuildInfo
    {
        public int? VersionCode { get; set; }
        public int? BuildType { get; set; }
        public string BuildKey { get; set; }
        public string VersionBaseName { get; set; }
        public string VersionDisplay { get; set; }
        public string Error { get; set; }

        public bool IsPreRestriction
        {
            get { return VersionCode.HasValue && VersionCode.Value < 413; }
        }

        public bool CanPatch
        {
            get { return VersionCode.HasValue && VersionCode.Value >= 413; }
        }

        public static string FormatBuildKey(int? versionCode, int? buildType)
        {
            if (!versionCode.HasValue || !buildType.HasValue) return null;
            return versionCode.Value.ToString(CultureInfo.InvariantCulture) + "." +
                   buildType.Value.ToString("D2", CultureInfo.InvariantCulture);
        }

        public static PclBuildInfo Probe(string path)
        {
            try
            {
                using (var target = PclTarget.Load(path))
                    return target.GetBuildInfo();
            }
            catch (Exception ex)
            {
                return new PclBuildInfo { Error = ex.Message };
            }
        }
    }
}
