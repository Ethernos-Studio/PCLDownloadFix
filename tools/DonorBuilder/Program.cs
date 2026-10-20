using Mono.Cecil;
using Mono.Cecil.Cil;

// Builds a tiny "restore donor" assembly containing the pre-restriction bodies of the
// five methods that PCL 2.99 gutted. The donor references PCL by assembly name, so the
// patcher can re-map every member reference onto the target PCL build.
//
// Usage: DonorBuilder <previous-pcl.exe> <embedded-deps-dir> <output.dll>

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: DonorBuilder <previous-pcl.exe> <deps-dir> <output.dll>");
    return 1;
}

var sourcePath = Path.GetFullPath(args[0]);
var depsDir = Path.GetFullPath(args[1]);
var outputPath = Path.GetFullPath(args[2]);

var specs = new[]
{
    new Spec("PCL.ModModpack", "ModpackInstall", Array.Empty<string>()),
    new Spec("PCL.ModModpack", "ModpackInstall", new[] { "System.String", "System.String", "System.String" }),
    new Spec("PCL.PageDownloadInstall", "LoaderInit", Array.Empty<string>()),
    new Spec("PCL.PageDownloadModify", "LoadInstanceFinished", new[] { "PCL.ModLoader/LoaderTask`2<PCL.ModMinecraft/McInstance,System.Object>" }),
    new Spec("PCL.PageDownloadResourceDetail", "InitializeStackResourceVersions", new[] { "System.Windows.Controls.StackPanel" }),
};

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(sourcePath)!);
resolver.AddSearchDirectory(depsDir);
resolver.AddSearchDirectory(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319");
resolver.AddSearchDirectory(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8");

using var source = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
{
    InMemory = true,
    ReadSymbols = false,
    AssemblyResolver = resolver
});

var donorName = new AssemblyNameDefinition("PCLDownloadRestore.Donor", new Version(1, 0, 0, 0));
using var donor = AssemblyDefinition.CreateAssembly(donorName, donorName.Name, ModuleKind.Dll);
var donorModule = donor.MainModule;
var donorType = new TypeDefinition(
    "PCLDownloadRestore", "RestoreMethods",
    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
    donorModule.TypeSystem.Object);
donorModule.Types.Add(donorType);

foreach (var spec in specs)
{
    var sourceMethod = FindMethod(source.MainModule, spec);
    var donorMethod = new MethodDefinition(
        "M_" + donorType.Methods.Count,
        MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
        donorModule.ImportReference(sourceMethod.ReturnType));

    if (sourceMethod.HasThis)
        donorMethod.Parameters.Add(new ParameterDefinition("thisArg", ParameterAttributes.None, donorModule.ImportReference(sourceMethod.DeclaringType)));
    foreach (var p in sourceMethod.Parameters)
        donorMethod.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, donorModule.ImportReference(p.ParameterType)));

    donorType.Methods.Add(donorMethod);
    CopyBody(sourceMethod, donorMethod, donorModule);
    Console.WriteLine($"donor {spec}: {sourceMethod.FullName} ({sourceMethod.Body.CodeSize} bytes) -> {donorMethod.Name}");
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
donor.Write(outputPath);
Console.WriteLine($"wrote {outputPath} ({new FileInfo(outputPath).Length} bytes)");
return 0;

static MethodDefinition FindMethod(ModuleDefinition module, Spec spec)
{
    var type = module.GetType(spec.Type) ?? throw new InvalidOperationException("type not found: " + spec.Type);
    foreach (var m in type.Methods)
    {
        if (m.Name != spec.Name || m.Parameters.Count != spec.ParamTypes.Length) continue;
        var ok = true;
        for (var i = 0; i < spec.ParamTypes.Length; i++)
        {
            if (m.Parameters[i].ParameterType.FullName != spec.ParamTypes[i]) { ok = false; break; }
        }
        if (ok) return m;
    }
    throw new InvalidOperationException("method not found: " + spec);
}

static void CopyBody(MethodDefinition source, MethodDefinition dest, ModuleDefinition donorModule)
{
    var sourceBody = source.Body;
    var body = new MethodBody(dest)
    {
        InitLocals = sourceBody.InitLocals,
        MaxStackSize = sourceBody.MaxStackSize
    };
    dest.Body = body;

    var variables = new Dictionary<VariableDefinition, VariableDefinition>();
    foreach (var v in sourceBody.Variables)
    {
        var nv = new VariableDefinition(donorModule.ImportReference(v.VariableType));
        body.Variables.Add(nv);
        variables[v] = nv;
    }

    var instructions = new Dictionary<Instruction, Instruction>();
    foreach (var ins in sourceBody.Instructions)
    {
        var ni = CreatePlaceholder(ins.OpCode, donorModule);
        instructions[ins] = ni;
        body.Instructions.Add(ni);
    }

    foreach (var ins in sourceBody.Instructions)
    {
        var ni = instructions[ins];
        ni.Operand = ins.Operand switch
        {
            null => null,
            Instruction target => instructions[target],
            Instruction[] targets => targets.Select(t => instructions[t]).ToArray(),
            VariableDefinition v => variables[v],
            ParameterDefinition p => dest.Parameters[p.Index],
            MethodReference mr => donorModule.ImportReference(mr),
            FieldReference fr => donorModule.ImportReference(fr),
            TypeReference tr => donorModule.ImportReference(tr),
            _ => ins.Operand
        };
    }

    foreach (var h in sourceBody.ExceptionHandlers)
    {
        var nh = new ExceptionHandler(h.HandlerType)
        {
            TryStart = h.TryStart is null ? null : instructions[h.TryStart],
            TryEnd = h.TryEnd is null ? null : instructions[h.TryEnd],
            HandlerStart = h.HandlerStart is null ? null : instructions[h.HandlerStart],
            HandlerEnd = h.HandlerEnd is null ? null : instructions[h.HandlerEnd],
            FilterStart = h.FilterStart is null ? null : instructions[h.FilterStart],
            CatchType = h.CatchType is null ? null : donorModule.ImportReference(h.CatchType)
        };
        body.ExceptionHandlers.Add(nh);
    }
}

static Instruction CreatePlaceholder(OpCode opcode, ModuleDefinition module) => opcode.OperandType switch
{
    OperandType.InlineNone => Instruction.Create(opcode),
    OperandType.InlineBrTarget => Instruction.Create(opcode, Instruction.Create(OpCodes.Nop)),
    OperandType.ShortInlineBrTarget => Instruction.Create(opcode, Instruction.Create(OpCodes.Nop)),
    OperandType.InlineSwitch => Instruction.Create(opcode, Array.Empty<Instruction>()),
    OperandType.InlineString => Instruction.Create(opcode, string.Empty),
    OperandType.InlineI => Instruction.Create(opcode, 0),
    OperandType.ShortInlineI => Instruction.Create(opcode, (sbyte)0),
    OperandType.InlineI8 => Instruction.Create(opcode, 0L),
    OperandType.ShortInlineR => Instruction.Create(opcode, 0f),
    OperandType.InlineR => Instruction.Create(opcode, 0d),
    OperandType.InlineVar => Instruction.Create(opcode, new VariableDefinition(module.TypeSystem.Object)),
    OperandType.ShortInlineVar => Instruction.Create(opcode, new VariableDefinition(module.TypeSystem.Object)),
    OperandType.InlineArg => Instruction.Create(opcode, new ParameterDefinition(module.TypeSystem.Object)),
    OperandType.ShortInlineArg => Instruction.Create(opcode, new ParameterDefinition(module.TypeSystem.Object)),
    OperandType.InlineType => Instruction.Create(opcode, module.TypeSystem.Object),
    OperandType.InlineTok => Instruction.Create(opcode, module.TypeSystem.Object),
    OperandType.InlineMethod => Instruction.Create(opcode, new MethodReference("placeholder", module.TypeSystem.Void, module.TypeSystem.Object)),
    OperandType.InlineField => Instruction.Create(opcode, new FieldReference("placeholder", module.TypeSystem.Object, module.TypeSystem.Object)),
    _ => throw new NotSupportedException("unsupported operand type " + opcode.OperandType)
};
sealed record Spec(string Type, string Name, string[] ParamTypes)
{
    public override string ToString() => Type + "::" + Name + "(" + string.Join(",", ParamTypes) + ")";
}


