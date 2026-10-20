using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PCLDownloadFix
{
    public sealed class ExtensibleResolver : IAssemblyResolver
    {
        private readonly DefaultAssemblyResolver _inner = new DefaultAssemblyResolver();

        public void AddSearchDirectory(string directory)
        {
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                _inner.AddSearchDirectory(directory);
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            return _inner.Resolve(name);
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            return _inner.Resolve(name, parameters);
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }

    public sealed class PclTarget : IDisposable
    {
        public const string GuardText = "为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……";
        public const string AssemblySimpleName = "Plain Craft Launcher 2";

        private readonly ExtensibleResolver _resolver;

        private PclTarget(string path, AssemblyDefinition assembly, ExtensibleResolver resolver)
        {
            Path = path;
            Assembly = assembly;
            _resolver = resolver;
        }

        public string Path { get; private set; }
        public AssemblyDefinition Assembly { get; private set; }
        public IAssemblyResolver Resolver { get { return _resolver; } }

        public static PclTarget Load(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new PatchException("未指定 PCL 程序路径。");
            var full = System.IO.Path.GetFullPath(path);
            if (!File.Exists(full)) throw new PatchException("找不到文件：" + full);

            var resolver = new ExtensibleResolver();
            resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(full));
            resolver.AddSearchDirectory(RuntimeEnvironment.GetRuntimeDirectory());
            resolver.AddSearchDirectory(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8");

            AssemblyDefinition assembly;
            try
            {
                assembly = AssemblyDefinition.ReadAssembly(full, new ReaderParameters
                {
                    InMemory = true,
                    ReadSymbols = false,
                    AssemblyResolver = resolver
                });
            }
            catch (BadImageFormatException ex)
            {
                throw new PatchException("这不是有效的 .NET 可执行文件，或文件已损坏。", ex);
            }
            catch (Exception ex)
            {
                throw new PatchException("读取程序集失败：" + ex.Message, ex);
            }

            if (!string.Equals(assembly.Name.Name, AssemblySimpleName, StringComparison.Ordinal))
            {
                assembly.Dispose();
                throw new PatchException("这不是 PCL 启动器（程序集名称：" + assembly.Name.Name + "）。");
            }

            return new PclTarget(full, assembly, resolver);
        }

        public string AssemblyVersion
        {
            get { return Assembly.Name.Version == null ? "" : Assembly.Name.Version.ToString(); }
        }

        public string ExtractDependencies(string tempRoot)
        {
            var resource = Assembly.MainModule.Resources
                .OfType<EmbeddedResource>()
                .FirstOrDefault(r => r.Name == "PCL.Resources.resources");
            if (resource == null)
                throw new PatchException("在 PCL 中找不到内嵌资源 PCL.Resources.resources，版本可能不受支持。");

            var dependencyDir = System.IO.Path.Combine(tempRoot, "deps");
            Directory.CreateDirectory(dependencyDir);

            var data = resource.GetResourceData();
            var count = 0;
            using (var stream = new MemoryStream(data))
            using (var reader = new ResourceReader(stream))
            {
                foreach (DictionaryEntry entry in reader)
                {
                    var bytes = entry.Value as byte[];
                    if (bytes == null || bytes.Length < 2 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') continue;

                    var name = Convert.ToString(entry.Key);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name += ".dll";
                    name = name.Replace('/', '_').Replace('\\', '_');

                    var target = System.IO.Path.Combine(dependencyDir, name);
                    File.WriteAllBytes(target, bytes);
                    count++;
                }
            }

            _resolver.AddSearchDirectory(dependencyDir);
            return dependencyDir;
        }

        public void AddSearchDirectory(string directory)
        {
            _resolver.AddSearchDirectory(directory);
        }

        public MethodDefinition TryFindMethod(MethodSpec spec)
        {
            var type = Assembly.MainModule.GetType(spec.TypeName);
            if (type == null) return null;

            foreach (var method in type.Methods)
            {
                if (!string.Equals(method.Name, spec.MethodName, StringComparison.Ordinal)) continue;
                if (method.Parameters.Count != spec.ParameterTypes.Length) continue;

                var match = true;
                for (var i = 0; i < spec.ParameterTypes.Length; i++)
                {
                    if (!string.Equals(method.Parameters[i].ParameterType.FullName, spec.ParameterTypes[i], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return method;
            }
            return null;
        }

        public MethodDefinition FindMethod(MethodSpec spec)
        {
            var method = TryFindMethod(spec);
            if (method == null) throw new PatchException("找不到方法：" + spec + "。此 PCL 版本可能不受支持。");
            return method;
        }

        public PatchState GetState(out string detail)
        {
            var loader = TryFindMethod(RestoreSpecs.All[2]);
            var modpack = TryFindMethod(RestoreSpecs.All[0]);
            var modpackFile = TryFindMethod(RestoreSpecs.All[1]);
            var resourceDetail = TryFindMethod(RestoreSpecs.All[4]);

            if (loader == null || modpack == null || modpackFile == null || resourceDetail == null)
            {
                detail = "缺少下载相关方法，可能不是 PCL 2.99 系列。";
                return PatchState.Unsupported;
            }

            var loaderRestored = ReferencesField(loader, "DlClientListLoader");
            var modpackRestored = ReferencesMethod(modpack, "SelectFile");
            var resourceRestored = ReferencesMethod(resourceDetail, "Install_Click") ||
                                   ReferencesMethod(resourceDetail, "_Lambda$__R18-5");
            var guard = HasGuard(modpack) || HasGuard(modpackFile) || HasGuard(loader);

            if (loaderRestored && modpackRestored && resourceRestored)
            {
                detail = "下载方法完整可用。";
                return PatchState.Patched;
            }
            if (loaderRestored || modpackRestored || resourceRestored)
            {
                detail = "只有部分下载方法被修改，可重新应用完整修复。";
                return PatchState.Partial;
            }
            if (guard)
            {
                detail = "检测到 2.99 的下载限制代码。";
                return PatchState.Original;
            }

            detail = "未找到 2.99 的下载限制代码，此构建可能已移除限制或结构不同。";
            return PatchState.Unsupported;
        }

        public static bool HasGuard(MethodDefinition method)
        {
            if (method == null || !method.HasBody) return false;
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode == OpCodes.Ldstr &&
                    string.Equals(instruction.Operand as string, GuardText, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public static bool ReferencesField(MethodDefinition method, string fieldName)
        {
            if (method == null || !method.HasBody) return false;
            foreach (var instruction in method.Body.Instructions)
            {
                var field = instruction.Operand as FieldReference;
                if (field != null && string.Equals(field.Name, fieldName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public static bool ReferencesMethod(MethodDefinition method, string methodName)
        {
            if (method == null || !method.HasBody) return false;
            foreach (var instruction in method.Body.Instructions)
            {
                var called = instruction.Operand as MethodReference;
                if (called != null && string.Equals(called.Name, methodName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (Assembly != null)
            {
                Assembly.Dispose();
                Assembly = null;
            }
        }
    }
}
