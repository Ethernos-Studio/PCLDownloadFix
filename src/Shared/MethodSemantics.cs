using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PCLDownloadFix
{
    public sealed class SemanticDiff
    {
        public SemanticDiff()
        {
            Changed = new List<string>();
            Added = new List<string>();
            Removed = new List<string>();
        }

        public List<string> Changed { get; private set; }
        public List<string> Added { get; private set; }
        public List<string> Removed { get; private set; }

        public bool IsEmpty
        {
            get { return Changed.Count == 0 && Added.Count == 0 && Removed.Count == 0; }
        }
    }

    public static class MethodSemantics
    {
        public static Dictionary<string, string> ComputeAll(AssemblyDefinition assembly)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var type in AllTypes(assembly.MainModule))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    result[method.FullName] = Compute(method);
                }
            }
            return result;
        }

        public static SemanticDiff Diff(Dictionary<string, string> before, Dictionary<string, string> after)
        {
            var diff = new SemanticDiff();
            foreach (var pair in before)
            {
                string value;
                if (!after.TryGetValue(pair.Key, out value))
                    diff.Removed.Add(pair.Key);
                else if (!string.Equals(pair.Value, value, StringComparison.Ordinal))
                    diff.Changed.Add(pair.Key);
            }
            foreach (var key in after.Keys)
            {
                if (!before.ContainsKey(key)) diff.Added.Add(key);
            }
            return diff;
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            foreach (var type in module.Types)
            {
                yield return type;
                foreach (var nested in AllNested(type)) yield return nested;
            }
        }

        private static IEnumerable<TypeDefinition> AllNested(TypeDefinition type)
        {
            foreach (var nested in type.NestedTypes)
            {
                yield return nested;
                foreach (var child in AllNested(nested)) yield return child;
            }
        }

        private static string Compute(MethodDefinition method)
        {
            var body = method.Body;
            var builder = new StringBuilder(1024);
            builder.Append(method.FullName).Append('\n');
            builder.Append("initlocals=").Append(body.InitLocals).Append('\n');

            foreach (var variable in body.Variables)
                builder.Append("local ").Append(variable.Index).Append(' ').Append(variable.VariableType.FullName).Append('\n');

            var index = new Dictionary<Instruction, int>();
            for (var i = 0; i < body.Instructions.Count; i++) index[body.Instructions[i]] = i;

            foreach (var instruction in body.Instructions)
            {
                builder.Append(index[instruction]).Append(' ')
                       .Append(instruction.OpCode.Code).Append(' ')
                       .Append(NormalizeOperand(instruction.Operand, index)).Append('\n');
            }

            foreach (var handler in body.ExceptionHandlers)
            {
                builder.Append("catch ").Append(handler.HandlerType)
                       .Append(" try=").Append(Index(handler.TryStart, index))
                       .Append('-').Append(Index(handler.TryEnd, index))
                       .Append(" handler=").Append(Index(handler.HandlerStart, index))
                       .Append('-').Append(Index(handler.HandlerEnd, index))
                       .Append(" filter=").Append(Index(handler.FilterStart, index))
                       .Append(" type=").Append(handler.CatchType == null ? "" : handler.CatchType.FullName)
                       .Append('\n');
            }

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                return HashUtil.ToHex(hash);
            }
        }

        private static string Index(Instruction instruction, Dictionary<Instruction, int> index)
        {
            if (instruction == null) return "-";
            int value;
            return index.TryGetValue(instruction, out value) ? value.ToString(CultureInfo.InvariantCulture) : "?";
        }

        private static string NormalizeOperand(object operand, Dictionary<Instruction, int> index)
        {
            if (operand == null) return "";
            if (operand is string) return "s:" + ((string)operand).Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
            if (operand is Instruction) return "il:" + Index((Instruction)operand, index);
            if (operand is Instruction[]) return "ils:" + string.Join(",", ((Instruction[])operand).Select(i => Index(i, index)));
            if (operand is MethodReference) return "m:" + ((MethodReference)operand).FullName;
            if (operand is FieldReference) return "f:" + ((FieldReference)operand).FullName;
            if (operand is TypeReference) return "t:" + ((TypeReference)operand).FullName;
            if (operand is VariableDefinition) return "v:" + ((VariableDefinition)operand).Index.ToString(CultureInfo.InvariantCulture);
            if (operand is ParameterDefinition) return "p:" + ((ParameterDefinition)operand).Index.ToString(CultureInfo.InvariantCulture);
            if (operand is CallSite) return "cs:" + ((CallSite)operand).FullName;
            return operand.GetType().Name + ":" + Convert.ToString(operand, CultureInfo.InvariantCulture);
        }
    }
}
