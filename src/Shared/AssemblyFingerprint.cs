using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace PCLDownloadFix
{
    /// <summary>
    /// Captures the parts of a PCL assembly that a patch must never change: identity and
    /// every managed resource. Method bodies are intentionally not included here.
    /// </summary>
    public sealed class AssemblyFingerprint
    {
        private readonly Dictionary<string, string> _resources;

        private AssemblyFingerprint(string name, string version, string culture, string publicKeyToken,
            string moduleKind, string runtime, Dictionary<string, string> resources)
        {
            Name = name;
            Version = version;
            Culture = culture;
            PublicKeyToken = publicKeyToken;
            ModuleKind = moduleKind;
            Runtime = runtime;
            _resources = resources;
        }

        public string Name { get; private set; }
        public string Version { get; private set; }
        public string Culture { get; private set; }
        public string PublicKeyToken { get; private set; }
        public string ModuleKind { get; private set; }
        public string Runtime { get; private set; }

        public static AssemblyFingerprint Capture(AssemblyDefinition assembly)
        {
            var module = assembly.MainModule;
            var resources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var resource in module.Resources)
            {
                var embedded = resource as EmbeddedResource;
                var payload = embedded == null
                    ? resource.ResourceType.ToString()
                    : HashUtil.Sha256Bytes(embedded.GetResourceData());
                resources[resource.Name] = resource.ResourceType + "|" + resource.Attributes + "|" + payload;
            }

            var token = assembly.Name.PublicKeyToken;
            return new AssemblyFingerprint(
                assembly.Name.Name,
                assembly.Name.Version == null ? "" : assembly.Name.Version.ToString(),
                assembly.Name.Culture ?? "",
                token == null ? "" : HashUtil.ToHex(token),
                module.Kind.ToString(),
                module.RuntimeVersion ?? "",
                resources);
        }

        public void EnsureSame(AssemblyFingerprint other, string context)
        {
            if (other == null)
                throw new PatchException(context + "无法读取程序集指纹。");

            var differences = new List<string>();
            if (!string.Equals(Name, other.Name, StringComparison.Ordinal))
                differences.Add("程序集名称 " + Name + " -> " + other.Name);
            if (!string.Equals(Version, other.Version, StringComparison.Ordinal))
                differences.Add("程序集版本 " + Version + " -> " + other.Version);
            if (!string.Equals(Culture, other.Culture, StringComparison.Ordinal))
                differences.Add("区域 " + Culture + " -> " + other.Culture);
            if (!string.Equals(PublicKeyToken, other.PublicKeyToken, StringComparison.Ordinal))
                differences.Add("公钥标记发生变化");
            if (!string.Equals(ModuleKind, other.ModuleKind, StringComparison.Ordinal))
                differences.Add("模块类型 " + ModuleKind + " -> " + other.ModuleKind);
            if (!string.Equals(Runtime, other.Runtime, StringComparison.Ordinal))
                differences.Add("运行时版本 " + Runtime + " -> " + other.Runtime);

            foreach (var pair in _resources)
            {
                string current;
                if (!other._resources.TryGetValue(pair.Key, out current))
                    differences.Add("资源丢失：" + pair.Key);
                else if (!string.Equals(pair.Value, current, StringComparison.Ordinal))
                    differences.Add("资源内容变化：" + pair.Key);
            }
            foreach (var name in other._resources.Keys)
            {
                if (!_resources.ContainsKey(name))
                    differences.Add("新增资源：" + name);
            }

            if (differences.Count > 0)
                throw new PatchException(context + "程序集结构或内嵌资源发生了变化，已中止操作：\r\n" +
                    string.Join("\r\n", differences.Take(10)));
        }
    }
}
