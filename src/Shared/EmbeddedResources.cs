using System;
using System.IO;
using System.Reflection;

namespace PCLDownloadFix
{
    public static class EmbeddedResources
    {
        public const string DonorName = "PCLDownloadFix.RestoreDonor.dll";
        public const string DonorSha256 = "e4e17c50cfcb33a804dbbc836b38d6349285df4d444b1ee9392e621588e2713d";
        public const string CecilName = "PCLDownloadFix.Mono.Cecil.dll";
        public const string CecilSha256 = "c41bdb9ffd3c5f6e17d2382c1012d73703e035e3f1100245fdd4e08c8dc6eb5b";

        public static byte[] Read(string name)
        {
            using (var stream = typeof(EmbeddedResources).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null) return null;
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        }

        public static Stream OpenDonor()
        {
            var data = Read(DonorName);
            if (data == null)
                throw new PatchException("程序内部损坏：找不到恢复模板资源。请重新下载本工具。");
            if (!string.Equals(HashUtil.Sha256Bytes(data), DonorSha256, StringComparison.OrdinalIgnoreCase))
                throw new PatchException("恢复模板校验失败，程序可能已损坏。请重新下载本工具。");
            return new MemoryStream(data, false);
        }
    }

    public sealed class SelfCheckResult
    {
        public string ExePath { get; set; }
        public long Size { get; set; }
        public string ExeSha256 { get; set; }
        public string DonorSha256 { get; set; }
        public bool DonorOk { get; set; }
        public string CecilSha256 { get; set; }
        public bool CecilOk { get; set; }

        public bool AllEmbeddedOk
        {
            get { return DonorOk && CecilOk; }
        }

        public string ProjectUrl
        {
            get { return DisclaimerText.ProjectUrl; }
        }
    }

    public static class SelfCheck
    {
        public static SelfCheckResult Run()
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var result = new SelfCheckResult
            {
                ExePath = assembly.Location,
                Size = new FileInfo(assembly.Location).Length,
                ExeSha256 = HashUtil.Sha256File(assembly.Location)
            };

            var donor = EmbeddedResources.Read(EmbeddedResources.DonorName);
            result.DonorSha256 = donor == null ? "" : HashUtil.Sha256Bytes(donor);
            result.DonorOk = donor != null &&
                             string.Equals(result.DonorSha256, EmbeddedResources.DonorSha256, StringComparison.OrdinalIgnoreCase);

            var cecil = EmbeddedResources.Read(EmbeddedResources.CecilName);
            result.CecilSha256 = cecil == null ? "" : HashUtil.Sha256Bytes(cecil);
            result.CecilOk = cecil != null &&
                             string.Equals(result.CecilSha256, EmbeddedResources.CecilSha256, StringComparison.OrdinalIgnoreCase);

            return result;
        }
    }
}
