using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PCLDownloadFix.Tests
{
    internal sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcldf-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; private set; }

        public string Path(string name)
        {
            return System.IO.Path.Combine(Root, name);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Builds a tiny PCL-shaped assembly that has the same type and method signatures the
    /// patcher looks for. It is not executable; it exists only to exercise detection,
    /// backup and manifest code without shipping a copyrighted PCL binary in the repo.
    /// </summary>
    internal static class SyntheticPcl
    {
        public static string Write(string path, bool guarded, bool loaderMarker = true,
            bool modpackMarker = true, bool resourceMarker = true, bool omitMethods = false,
            Version version = null)
        {
            version = version ?? new Version(2, 99, 0, 0);
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("Plain Craft Launcher 2", version),
                "Plain Craft Launcher 2",
                ModuleKind.Dll);
            try
            {
                var module = assembly.MainModule;
                var modModpack = AddType(module, "PCL", "ModModpack");
                var pageInstall = AddType(module, "PCL", "PageDownloadInstall");
                var pageModify = AddType(module, "PCL", "PageDownloadModify");
                var pageResource = AddType(module, "PCL", "PageDownloadResourceDetail");

                var modLoader = AddType(module, "PCL", "ModLoader");
                var loaderTask = new TypeDefinition("", "LoaderTask`2",
                    TypeAttributes.NestedPublic | TypeAttributes.Class, module.TypeSystem.Object);
                modLoader.NestedTypes.Add(loaderTask);
                loaderTask.GenericParameters.Add(new GenericParameter("T1", loaderTask));
                loaderTask.GenericParameters.Add(new GenericParameter("T2", loaderTask));

                var modMinecraft = AddType(module, "PCL", "ModMinecraft");
                var mcInstance = new TypeDefinition("", "McInstance",
                    TypeAttributes.NestedPublic | TypeAttributes.Class, module.TypeSystem.Object);
                modMinecraft.NestedTypes.Add(mcInstance);

                var loaderTaskType = new GenericInstanceType(loaderTask);
                loaderTaskType.GenericArguments.Add(mcInstance);
                loaderTaskType.GenericArguments.Add(module.TypeSystem.Object);

                var stackPanel = new TypeReference("System.Windows.Controls", "StackPanel",
                    module, module.TypeSystem.CoreLibrary);

                var loaderField = new FieldDefinition("DlClientListLoader",
                    FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Object);
                pageInstall.Fields.Add(loaderField);

                var selectFile = AddMethod(modModpack, "SelectFile", true, module.TypeSystem.Void);
                EmitRet(selectFile);

                var installClick = AddMethod(pageResource, "Install_Click", true, module.TypeSystem.Void);
                EmitRet(installClick);

                if (omitMethods) return WriteAndClose(assembly, path);

                var modpackInstall = AddMethod(modModpack, "ModpackInstall", true, module.TypeSystem.Void);
                if (modpackMarker)
                {
                    var il = modpackInstall.Body.GetILProcessor();
                    il.Emit(OpCodes.Call, selectFile);
                    il.Emit(OpCodes.Ret);
                }
                else
                {
                    EmitRet(modpackInstall);
                }

                var modpackInstallArgs = AddMethod(modModpack, "ModpackInstall", true, module.TypeSystem.Void,
                    module.TypeSystem.String, module.TypeSystem.String, module.TypeSystem.String);
                if (guarded)
                {
                    var il = modpackInstallArgs.Body.GetILProcessor();
                    il.Emit(OpCodes.Ldstr, PclTarget.GuardText);
                    il.Emit(OpCodes.Pop);
                    il.Emit(OpCodes.Ret);
                }
                else
                {
                    EmitRet(modpackInstallArgs);
                }

                var loaderInit = AddMethod(pageInstall, "LoaderInit", false, module.TypeSystem.Void);
                if (loaderMarker)
                {
                    var il = loaderInit.Body.GetILProcessor();
                    il.Emit(OpCodes.Ldsfld, loaderField);
                    il.Emit(OpCodes.Pop);
                    il.Emit(OpCodes.Ret);
                }
                else
                {
                    EmitRet(loaderInit);
                }

                var loadFinished = AddMethod(pageModify, "LoadInstanceFinished", false,
                    module.TypeSystem.Void, loaderTaskType);
                EmitRet(loadFinished);

                var initialize = AddMethod(pageResource, "InitializeStackResourceVersions", false,
                    module.TypeSystem.Void, stackPanel);
                if (resourceMarker)
                {
                    var il = initialize.Body.GetILProcessor();
                    il.Emit(OpCodes.Call, installClick);
                    il.Emit(OpCodes.Ret);
                }
                else
                {
                    EmitRet(initialize);
                }

                return WriteAndClose(assembly, path);
            }
            catch
            {
                assembly.Dispose();
                throw;
            }
        }

        private static string WriteAndClose(AssemblyDefinition assembly, string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            assembly.Write(path);
            assembly.Dispose();
            return path;
        }

        private static TypeDefinition AddType(ModuleDefinition module, string ns, string name)
        {
            var type = new TypeDefinition(ns, name,
                TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(type);
            return type;
        }

        private static MethodDefinition AddMethod(TypeDefinition type, string name, bool isStatic,
            TypeReference returnType, params TypeReference[] parameterTypes)
        {
            var attributes = MethodAttributes.Public | MethodAttributes.HideBySig;
            if (isStatic) attributes |= MethodAttributes.Static;

            var method = new MethodDefinition(name, attributes, returnType);
            foreach (var parameterType in parameterTypes)
                method.Parameters.Add(new ParameterDefinition(parameterType));
            method.Body = new MethodBody(method) { InitLocals = false, MaxStackSize = 8 };
            type.Methods.Add(method);
            return method;
        }

        private static void EmitRet(MethodDefinition method)
        {
            method.Body.GetILProcessor().Emit(OpCodes.Ret);
        }
    }
}
