using System;
using System.Collections.Generic;

namespace PCLDownloadFix
{
    public sealed class MethodSpec
    {
        public MethodSpec(string typeName, string methodName, params string[] parameterTypes)
        {
            TypeName = typeName;
            MethodName = methodName;
            ParameterTypes = parameterTypes ?? new string[0];
        }

        public string TypeName { get; private set; }
        public string MethodName { get; private set; }
        public string[] ParameterTypes { get; private set; }

        public override string ToString()
        {
            return TypeName + "::" + MethodName +
                   (ParameterTypes.Length == 0 ? "()" : "(" + string.Join(", ", ParameterTypes) + ")");
        }
    }

    public static class RestoreSpecs
    {
        public static readonly MethodSpec[] All =
        {
            new MethodSpec("PCL.ModModpack", "ModpackInstall"),
            new MethodSpec("PCL.ModModpack", "ModpackInstall", "System.String", "System.String", "System.String"),
            new MethodSpec("PCL.PageDownloadInstall", "LoaderInit"),
            new MethodSpec("PCL.PageDownloadModify", "LoadInstanceFinished", "PCL.ModLoader/LoaderTask`2<PCL.ModMinecraft/McInstance,System.Object>"),
            new MethodSpec("PCL.PageDownloadResourceDetail", "InitializeStackResourceVersions", "System.Windows.Controls.StackPanel")
        };

        public static List<string> DisplayNames()
        {
            var list = new List<string>();
            foreach (var spec in All) list.Add(spec.ToString());
            return list;
        }
    }

    public enum PatchState
    {
        Unknown,
        Original,
        Patched,
        Partial,
        Unsupported,
        Error
    }

    public sealed class PclTargetInfo
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
        public string FileVersion { get; set; }
        public string AssemblyVersion { get; set; }
        public string BuildLabel { get; set; }
        public PatchState State { get; set; }
        public bool NoFixNeeded { get; set; }
        public string StateText
        {
            get
            {
                switch (State)
                {
                    case PatchState.Original: return "原版（下载功能受限）";
                    case PatchState.Patched: return NoFixNeeded ? "限制前版本（无需修复）" : "已修复";
                    case PatchState.Partial: return "部分修复";
                    case PatchState.Unsupported: return "不支持或无法识别";
                    case PatchState.Error: return "读取失败";
                    default: return "未知";
                }
            }
        }
        public bool IsSupported { get; set; }
        public string Message { get; set; }
        public string BackupPath { get; set; }
        public bool BackupExists { get; set; }
        public bool BackupIsValidOriginal { get; set; }
        public string BackupSha256 { get; set; }

        public string BackupText
        {
            get
            {
                if (!BackupExists) return "无";
                return BackupIsValidOriginal ? "已存在（原版备份）" : "已存在，但内容不是原版";
            }
        }
    }
}
