using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace PCLDownloadFix.Tests
{
    public class MethodSemanticsTests
    {
        [Fact]
        public void ComputeAndDiff_DetectsChangedMethodOnly()
        {
            using (var workspace = new TempWorkspace())
            {
                var path = SyntheticPcl.Write(workspace.Path("pcl.dll"), false);
                using (var assembly = AssemblyDefinition.ReadAssembly(path))
                {
                    var before = MethodSemantics.ComputeAll(assembly);
                    var type = assembly.MainModule.GetType("PCL.PageDownloadInstall");
                    var method = type.Methods[0];
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));

                    var after = MethodSemantics.ComputeAll(assembly);
                    var diff = MethodSemantics.Diff(before, after);

                    Assert.False(diff.IsEmpty);
                    Assert.Contains(method.FullName, diff.Changed);
                    Assert.Single(diff.Changed);
                    Assert.Empty(diff.Added);
                    Assert.Empty(diff.Removed);
                }
            }
        }

        [Fact]
        public void Diff_ReportsAddedAndRemovedMethods()
        {
            var before = new Dictionary<string, string> { { "A", "1" }, { "B", "2" } };
            var after = new Dictionary<string, string> { { "A", "1" }, { "C", "3" } };
            var diff = MethodSemantics.Diff(before, after);

            Assert.Empty(diff.Changed);
            Assert.Contains("B", diff.Removed);
            Assert.Contains("C", diff.Added);
        }
    }
}
