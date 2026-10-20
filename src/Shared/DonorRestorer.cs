using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PCLDownloadFix
{
    /// <summary>
    /// Copies the pre-restriction method bodies from the embedded donor assembly onto the
    /// target PCL assembly. Every metadata reference is re-mapped by name, so the donor does
    /// not need to be the same build as the target.
    /// </summary>
    public sealed class DonorRestorer : IDisposable
    {
        private readonly PclTarget _target;
        private readonly AssemblyDefinition _donor;
        private readonly ModuleDefinition _targetModule;
        private readonly string _targetAssemblyName;

        public DonorRestorer(PclTarget target, Stream donorStream)
        {
            _target = target;
            _targetModule = target.Assembly.MainModule;
            _targetAssemblyName = target.Assembly.Name.Name;

            var resolver = new TargetResolver(target.Assembly, target.Resolver);
            _donor = AssemblyDefinition.ReadAssembly(donorStream, new ReaderParameters
            {
                InMemory = true,
                ReadSymbols = false,
                AssemblyResolver = resolver
            });
        }

        public IList<string> Restore(Action<LogEntry> log)
        {
            var donorType = _donor.MainModule.GetType("PCLDownloadRestore.RestoreMethods");
            if (donorType == null)
                throw new PatchException("恢复模板损坏：找不到 RestoreMethods。");
            if (donorType.Methods.Count != RestoreSpecs.All.Length)
                throw new PatchException("恢复模板不完整：应有 " + RestoreSpecs.All.Length + " 个方法，实际有 " + donorType.Methods.Count + " 个。");

            var restored = new List<string>();
            for (var i = 0; i < RestoreSpecs.All.Length; i++)
            {
                var spec = RestoreSpecs.All[i];
                var donorMethod = donorType.Methods[i];
                ValidateDonorMethod(donorMethod, spec, i);
                var targetMethod = _target.FindMethod(spec);

                Log.Write(log, LogLevel.Info, "恢复 " + spec.MethodName + "（" + donorMethod.Body.CodeSize + " 字节 IL）");
                CopyBody(donorMethod, targetMethod);
                restored.Add(targetMethod.FullName);
            }
            return restored;
        }

        private static void ValidateDonorMethod(MethodDefinition method, MethodSpec spec, int index)
        {
            if (method == null || !method.HasBody)
                throw new PatchException("恢复模板损坏：第 " + (index + 1) + " 个方法没有方法体。");
            if (!method.IsStatic)
                throw new PatchException("恢复模板损坏：第 " + (index + 1) + " 个方法不是静态方法。");

            // Donor methods are static. Instance methods on the original build carry the
            // receiver as an extra first parameter; static methods do not.
            var hasReceiver = method.Parameters.Count == spec.ParameterTypes.Length + 1;
            if (method.Parameters.Count != spec.ParameterTypes.Length && !hasReceiver)
                throw new PatchException("恢复模板与目标方法不匹配：第 " + (index + 1) + " 个方法参数数量不一致。");

            var offset = hasReceiver ? 1 : 0;
            if (hasReceiver &&
                !string.Equals(method.Parameters[0].ParameterType.FullName, spec.TypeName, StringComparison.Ordinal))
            {
                throw new PatchException("恢复模板与目标方法不匹配：第 " + (index + 1) + " 个方法的所属类型不是 " + spec.TypeName + "。");
            }

            for (var i = 0; i < spec.ParameterTypes.Length; i++)
            {
                if (!string.Equals(method.Parameters[i + offset].ParameterType.FullName, spec.ParameterTypes[i], StringComparison.Ordinal))
                    throw new PatchException("恢复模板与目标方法不匹配：" + spec + " 的第 " + (i + 1) + " 个参数类型不一致。");
            }
        }

        private void CopyBody(MethodDefinition donorMethod, MethodDefinition targetMethod)
        {
            var source = donorMethod.Body;
            var body = new MethodBody(targetMethod)
            {
                InitLocals = source.InitLocals,
                MaxStackSize = source.MaxStackSize
            };
            targetMethod.Body = body;

            var variables = new Dictionary<VariableDefinition, VariableDefinition>();
            foreach (var variable in source.Variables)
            {
                var mapped = new VariableDefinition(MapType(variable.VariableType));
                body.Variables.Add(mapped);
                variables[variable] = mapped;
            }

            var instructions = new Dictionary<Instruction, Instruction>();
            foreach (var instruction in source.Instructions)
            {
                var mapped = CreatePlaceholder(instruction.OpCode);
                instructions[instruction] = mapped;
                body.Instructions.Add(mapped);
            }

            foreach (var instruction in source.Instructions)
            {
                var mapped = instructions[instruction];
                var parameter = instruction.Operand as ParameterDefinition;
                if (parameter != null && targetMethod.HasThis && parameter.Index == 0 && mapped.OpCode == OpCodes.Ldarg)
                {
                    // "ldarg this" is normally encoded as ldarg.0 with no operand.
                    mapped.OpCode = OpCodes.Ldarg_0;
                    mapped.Operand = null;
                    continue;
                }

                mapped.Operand = instruction.Operand == null
                    ? null
                    : MapOperand(instruction.Operand, targetMethod, variables, instructions);
            }

            foreach (var handler in source.ExceptionHandlers)
            {
                body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
                {
                    TryStart = MapInstruction(handler.TryStart, instructions),
                    TryEnd = MapInstruction(handler.TryEnd, instructions),
                    HandlerStart = MapInstruction(handler.HandlerStart, instructions),
                    HandlerEnd = MapInstruction(handler.HandlerEnd, instructions),
                    FilterStart = MapInstruction(handler.FilterStart, instructions),
                    CatchType = handler.CatchType == null ? null : MapType(handler.CatchType)
                });
            }
        }

        private object MapOperand(object operand, MethodDefinition targetMethod,
            Dictionary<VariableDefinition, VariableDefinition> variables,
            Dictionary<Instruction, Instruction> instructions)
        {
            if (operand is Instruction) return instructions[(Instruction)operand];
            if (operand is Instruction[]) return ((Instruction[])operand).Select(i => instructions[i]).ToArray();
            if (operand is VariableDefinition)
            {
                VariableDefinition mapped;
                if (variables.TryGetValue((VariableDefinition)operand, out mapped)) return mapped;
                throw new PatchException("恢复模板中的局部变量映射失败。");
            }
            if (operand is ParameterDefinition)
                return MapParameter((ParameterDefinition)operand, targetMethod);
            if (operand is MethodReference) return MapMethod((MethodReference)operand);
            if (operand is FieldReference) return MapField((FieldReference)operand);
            if (operand is TypeReference) return MapType((TypeReference)operand);
            if (operand is string) return operand;
            return operand;
        }

        private static Instruction MapInstruction(Instruction instruction, Dictionary<Instruction, Instruction> map)
        {
            if (instruction == null) return null;
            Instruction mapped;
            return map.TryGetValue(instruction, out mapped) ? mapped : null;
        }

        private static ParameterDefinition MapParameter(ParameterDefinition parameter, MethodDefinition target)
        {
            if (target.HasThis)
            {
                if (parameter.Index == 0) return null;
                return target.Parameters[parameter.Index - 1];
            }
            return target.Parameters[parameter.Index];
        }

        private MethodReference MapMethod(MethodReference method)
        {
            if (!IsPcl(method.DeclaringType))
                return _targetModule.ImportReference(method);

            var generic = method as GenericInstanceMethod;
            if (generic != null)
            {
                MethodDefinition element = null;
                try { element = generic.ElementMethod.Resolve(); }
                catch (Exception) { }
                if (element == null) element = TryCreateMissingCallback(generic.ElementMethod);
                if (element == null)
                    throw new PatchException("无法在目标 PCL 中解析方法：" + generic.ElementMethod.FullName);

                var mapped = new GenericInstanceMethod(element);
                foreach (var argument in generic.GenericArguments)
                    mapped.GenericArguments.Add(MapType(argument));
                return mapped;
            }

            MethodDefinition definition = null;
            try { definition = method.Resolve(); }
            catch (Exception) { }
            if (definition == null) definition = TryCreateMissingCallback(method);
            if (definition == null)
                throw new PatchException("无法在目标 PCL 中解析方法：" + method.FullName);

            if (method.DeclaringType is GenericInstanceType)
            {
                var mapped = new MethodReference(definition.Name, Substitute(method.ReturnType, method.DeclaringType), MapType(method.DeclaringType))
                {
                    HasThis = definition.HasThis,
                    ExplicitThis = definition.ExplicitThis,
                    CallingConvention = definition.CallingConvention
                };
                foreach (var parameter in method.Parameters)
                    mapped.Parameters.Add(new ParameterDefinition(Substitute(parameter.ParameterType, method.DeclaringType)));
                return mapped;
            }

            return definition;
        }

        private FieldReference MapField(FieldReference field)
        {
            if (!IsPcl(field.DeclaringType))
                return _targetModule.ImportReference(field);

            var definition = field.Resolve();
            if (definition == null)
                throw new PatchException("无法在目标 PCL 中解析字段：" + field.FullName);

            if (field.DeclaringType is GenericInstanceType)
            {
                return new FieldReference(
                    definition.Name,
                    Substitute(field.FieldType, field.DeclaringType),
                    MapType(field.DeclaringType));
            }

            return definition;
        }

        private TypeReference Substitute(TypeReference type, TypeReference declaringType)
        {
            var parameter = type as GenericParameter;
            if (parameter != null && parameter.Type == GenericParameterType.Type)
            {
                var generic = declaringType as GenericInstanceType;
                if (generic != null && parameter.Position < generic.GenericArguments.Count)
                    return MapType(generic.GenericArguments[parameter.Position]);
            }

            var instance = type as GenericInstanceType;
            if (instance != null)
            {
                var mapped = new GenericInstanceType(Substitute(instance.ElementType, declaringType));
                foreach (var argument in instance.GenericArguments)
                    mapped.GenericArguments.Add(Substitute(argument, declaringType));
                return mapped;
            }

            if (type is ArrayType)
            {
                var array = (ArrayType)type;
                return new ArrayType(Substitute(array.ElementType, declaringType), array.Rank);
            }
            if (type is ByReferenceType)
                return new ByReferenceType(Substitute(((ByReferenceType)type).ElementType, declaringType));
            if (type is PointerType)
                return new PointerType(Substitute(((PointerType)type).ElementType, declaringType));

            return MapType(type);
        }

        private TypeReference MapType(TypeReference type)
        {
            var instance = type as GenericInstanceType;
            if (instance != null)
            {
                var mapped = new GenericInstanceType(MapType(instance.ElementType));
                foreach (var argument in instance.GenericArguments)
                    mapped.GenericArguments.Add(MapType(argument));
                return mapped;
            }

            if (type is ArrayType)
            {
                var array = (ArrayType)type;
                var mapped = new ArrayType(MapType(array.ElementType), array.Rank);
                for (var i = 0; i < array.Dimensions.Count; i++)
                    mapped.Dimensions[i] = array.Dimensions[i];
                return mapped;
            }
            if (type is ByReferenceType)
                return new ByReferenceType(MapType(((ByReferenceType)type).ElementType));
            if (type is PointerType)
                return new PointerType(MapType(((PointerType)type).ElementType));
            if (type is PinnedType)
                return new PinnedType(MapType(((PinnedType)type).ElementType));
            if (type is OptionalModifierType)
            {
                var modifier = (OptionalModifierType)type;
                return new OptionalModifierType(MapType(modifier.ModifierType), MapType(modifier.ElementType));
            }
            if (type is RequiredModifierType)
            {
                var modifier = (RequiredModifierType)type;
                return new RequiredModifierType(MapType(modifier.ModifierType), MapType(modifier.ElementType));
            }
            if (type is GenericParameter)
                throw new PatchException("恢复模板中存在未替换的泛型参数：" + type.FullName);

            if (IsPcl(type))
            {
                var resolved = type.Resolve();
                if (resolved == null)
                    throw new PatchException("无法在目标 PCL 中解析类型：" + type.FullName);
                return resolved;
            }

            return _targetModule.ImportReference(type);
        }

        private MethodDefinition TryCreateMissingCallback(MethodReference method)
        {
            if (!string.Equals(method.Name, "_Lambda$__R3-3", StringComparison.Ordinal)) return null;
            if (method.DeclaringType == null || method.DeclaringType.Name != "PageDownloadInstall") return null;

            var type = _targetModule.GetType("PCL.PageDownloadInstall");
            if (type == null) return null;

            foreach (var existing in type.Methods)
            {
                if (string.Equals(existing.Name, method.Name, StringComparison.Ordinal) &&
                    existing.Parameters.Count == method.Parameters.Count)
                    return existing;
            }

            var callback = type.Methods.FirstOrDefault(m =>
                string.Equals(m.Name, "LoadMinecraft_OnFinish", StringComparison.Ordinal) && m.Parameters.Count == 0);
            if (callback == null) return null;

            var lambda = new MethodDefinition(method.Name,
                MethodAttributes.Private | MethodAttributes.HideBySig,
                _targetModule.TypeSystem.Void);
            foreach (var parameter in method.Parameters)
                lambda.Parameters.Add(new ParameterDefinition(MapType(parameter.ParameterType)));
            type.Methods.Add(lambda);

            var il = lambda.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, callback);
            il.Emit(OpCodes.Ret);
            return lambda;
        }

        private bool IsPcl(TypeReference type)
        {
            while (type is TypeSpecification && !(type is GenericInstanceType))
                type = ((TypeSpecification)type).ElementType;
            var instance = type as GenericInstanceType;
            if (instance != null) type = instance.ElementType;
            if (type is GenericParameter) return false;

            var reference = type.Scope as AssemblyNameReference;
            if (reference != null)
                return string.Equals(reference.Name, _targetAssemblyName, StringComparison.Ordinal);

            var module = type.Scope as ModuleDefinition;
            return module != null && module.Assembly != null &&
                   string.Equals(module.Assembly.Name.Name, _targetAssemblyName, StringComparison.Ordinal);
        }

        private Instruction CreatePlaceholder(OpCode opcode)
        {
            switch (opcode.OperandType)
            {
                case OperandType.InlineNone: return Instruction.Create(opcode);
                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineBrTarget: return Instruction.Create(opcode, Instruction.Create(OpCodes.Nop));
                case OperandType.InlineSwitch: return Instruction.Create(opcode, new Instruction[0]);
                case OperandType.InlineString: return Instruction.Create(opcode, string.Empty);
                case OperandType.InlineI: return Instruction.Create(opcode, 0);
                case OperandType.ShortInlineI: return Instruction.Create(opcode, (sbyte)0);
                case OperandType.InlineI8: return Instruction.Create(opcode, 0L);
                case OperandType.ShortInlineR: return Instruction.Create(opcode, 0f);
                case OperandType.InlineR: return Instruction.Create(opcode, 0d);
                case OperandType.InlineVar:
                case OperandType.ShortInlineVar: return Instruction.Create(opcode, new VariableDefinition(_targetModule.TypeSystem.Object));
                case OperandType.InlineArg:
                case OperandType.ShortInlineArg: return Instruction.Create(opcode, new ParameterDefinition(_targetModule.TypeSystem.Object));
                case OperandType.InlineType:
                case OperandType.InlineTok: return Instruction.Create(opcode, _targetModule.TypeSystem.Object);
                case OperandType.InlineMethod: return Instruction.Create(opcode, new MethodReference("placeholder", _targetModule.TypeSystem.Void, _targetModule.TypeSystem.Object));
                case OperandType.InlineField: return Instruction.Create(opcode, new FieldReference("placeholder", _targetModule.TypeSystem.Object, _targetModule.TypeSystem.Object));
                default: throw new PatchException("不支持的 IL 操作数类型：" + opcode.OperandType);
            }
        }

        public void Dispose()
        {
            if (_donor != null) _donor.Dispose();
        }

        private sealed class TargetResolver : IAssemblyResolver
        {
            private readonly AssemblyDefinition _target;
            private readonly IAssemblyResolver _fallback;

            public TargetResolver(AssemblyDefinition target, IAssemblyResolver fallback)
            {
                _target = target;
                _fallback = fallback;
            }

            public AssemblyDefinition Resolve(AssemblyNameReference name)
            {
                return Resolve(name, new ReaderParameters());
            }

            public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
            {
                if (string.Equals(name.Name, _target.Name.Name, StringComparison.Ordinal)) return _target;
                return _fallback.Resolve(name, parameters);
            }

            public void Dispose() { }
        }
    }
}

