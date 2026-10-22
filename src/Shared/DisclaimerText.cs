using System;
using System.Reflection;

namespace PCLDownloadFix
{
    public static class DisclaimerText
    {
        public const string Author = "Ethernos Studio";
        public const string ProductName = "PCL 下载修复器（第三方）";
        public const string ProjectUrl = "https://github.com/Ethernos-Studio/PCLDownloadFix";
        public const string ReferenceUrl = "https://github.com/Jelly10086/PCL-Re.Download_PATCH";

        public static string ProductVersion
        {
            get
            {
                var assembly = typeof(DisclaimerText).Assembly;
                var informational = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    assembly, typeof(AssemblyInformationalVersionAttribute));
                if (informational != null && !string.IsNullOrEmpty(informational.InformationalVersion))
                    return informational.InformationalVersion.Split('+')[0];

                var version = assembly.GetName().Version;
                return version == null ? "0.0.0" : version.ToString(3);
            }
        }

        public static readonly string[] Paragraphs =
        {
            "本工具由 Ethernos Studio 开发，是第三方项目，与 PCL（Plain Craft Launcher）官方及龙腾猫跃无关，未获得官方背书。",
            "PCL 及其相关内容的著作权归龙腾猫跃所有。请遵守 PCL 的分发许可，仅将本工具用于个人、非商业用途；不得倒卖、二次打包收费或用于违反当地法律法规的用途。",
            "HMCL 是独立开源项目，本工具与 HMCL 官方无关。HMCL 修复会替换 JAR 内的 RegionUtils 类；修复后 HMCL 自带的完整性签名会失效，请自行评估并遵守 GPL 许可，仅用于个人副本。",
            "本工具会修改 PCL 主程序（Plain Craft Launcher 2.exe）或 HMCL 的 JAR/EXE，并默认在同目录创建 .pcl-original.bak / .hmcl-original.bak 原版备份。操作前请完全关闭目标启动器，并自行备份重要数据。",
            "为什么可能被杀毒软件拦截：本工具没有代码签名，且需要读取并改写 PCL 主程序的 IL；EXE 内还嵌入了 Mono.Cecil 与 IL 恢复模板。这些行为与“补丁器 / 破解器”的启发式特征重合，可能被误报为 HackTool、Patcher、Trojan 等。本工具不联网、不注入进程、不写启动项、不收集信息，完整源代码随发布包提供，可自行编译核验。",
            "验证本软件是否被修改：官方发布包内附 SHA256SUMS.txt。在 PowerShell 中运行 Get-FileHash .\\PCL下载修复器.exe -Algorithm SHA256，将结果与 GitHub Release 公布的 SHA-256 比对；也可以运行 PCL下载修复器-控制台.exe selfcheck，查看本机 EXE 的哈希和内嵌组件校验结果。若哈希一致，说明文件与官方发布版本相同。高级用户可直接使用包内 source 目录重新编译。",
            "项目主页（GitHub）：" + ProjectUrl + " ｜ 参考项目（Jelly10086）：" + ReferenceUrl,
            "本工具按“现状”提供，不保证与任何特定 PCL 版本兼容，也不保证下载功能永久可用。因使用或无法使用本工具造成的任何直接或间接损失，开发者不承担责任。",
            "下载与使用 Minecraft 相关内容时，请遵守 Mojang / Microsoft 的最终用户许可协议及所在地法律法规。",
            "本工具参考了 Jelly10086 的 PCL-Re.Download_PATCH 方案，并使用 MIT 许可的 Mono.Cecil；相关署名与许可见 THIRD-PARTY-NOTICES.txt。",
            "点击“我已知晓，继续”即表示你已阅读、理解并同意上述内容。"
        };

        public static string FullText
        {
            get { return string.Join(Environment.NewLine + Environment.NewLine, Paragraphs); }
        }
    }
}
