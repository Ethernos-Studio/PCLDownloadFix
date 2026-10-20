using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

static string NormalizeOperand(object? operand)
{
    switch (operand)
    {
        case null:
            return "";
        case string s:
            return "str:" + s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r");
        case Instruction ins:
            return "il:" + ins.Offset.ToString("X4");
        case Instruction[] targets:
            return "ils:" + string.Join(",", targets.Select(t => t.Offset.ToString("X4")));
        case MethodReference mr:
            return "m:" + mr.FullName;
        case FieldReference fr:
            return "f:" + fr.FullName;
        case TypeReference tr:
            return "t:" + tr.FullName;
        case VariableDefinition v:
            return "v:" + v.Index;
        case ParameterDefinition p:
            return "p:" + p.Index;
        case CallSite cs:
            return "cs:" + cs.FullName;
        default:
            return operand.GetType().Name + ":" + Convert.ToString(operand, System.Globalization.CultureInfo.InvariantCulture);
    }
}

static string Semantics(MethodDefinition m)
{
    var sb = new StringBuilder();
    foreach (var ins in m.Body.Instructions)
        sb.Append(ins.OpCode.Code).Append(' ').Append(NormalizeOperand(ins.Operand)).Append('\n');
    return sb.ToString();
}

if (args.Length == 0)
{
    Console.WriteLine("Usage: MethodDiff dump <exe> <out.tsv> | diff <a.tsv> <b.tsv>");
    return 1;
}

if (args[0] == "dump")
{
    var asmPath = args[1];
    var outPath = args[2];
    using var asm = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters { InMemory = true, ReadSymbols = false });
    var lines = new List<string> { "type\tmethod\trva\tcodeSize\thash" };
    void Walk(TypeDefinition type)
    {
        foreach (var m in type.Methods)
        {
            if (!m.HasBody) continue;
            var key = m.FullName.Replace("\t", " ");
            var text = Semantics(m);
            lines.Add($"{type.FullName}\t{key}\t0x{m.RVA:X8}\t{m.Body.CodeSize}\t{Hash(Encoding.UTF8.GetBytes(text))}");
        }
        foreach (var n in type.NestedTypes) Walk(n);
    }
    foreach (var t in asm.MainModule.Types) Walk(t);
    File.WriteAllLines(outPath, lines, new UTF8Encoding(false));
    Console.WriteLine($"Wrote {lines.Count - 1} methods to {outPath}");
    return 0;
}

if (args[0] == "il")
{
    using var asm = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { InMemory = true, ReadSymbols = false });
    var type = asm.MainModule.GetType(args[2]) ?? throw new InvalidOperationException("type not found");
    foreach (var m in type.Methods.Where(m => m.Name == args[3]))
    {
        Console.WriteLine("=== " + m.FullName);
        foreach (var ins in m.Body.Instructions)
            Console.WriteLine($"{ins.Offset:X4} {ins.OpCode.Code} {NormalizeOperand(ins.Operand)}");
    }
    return 0;
}

if (args[0] == "extract")
{
    using var asm = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { InMemory = true, ReadSymbols = false });
    var outDir = args[2];
    Directory.CreateDirectory(outDir);
    foreach (var res in asm.MainModule.Resources)
    {
        if (res is not EmbeddedResource er) continue;
        var data = er.GetResourceData();
        File.WriteAllBytes(Path.Combine(outDir, res.Name), data);
        Console.WriteLine($"resource {res.Name} -> {data.Length} bytes");
        if (res.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream(data);
            using var reader = new System.Resources.ResourceReader(ms);
            foreach (System.Collections.DictionaryEntry entry in reader)
            {
                if (entry.Value is byte[] bytes)
                {
                    var name = entry.Key.ToString()!;
                    if (bytes.Length > 2 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z')
                    {
                        var safe = name.Replace('/', '_');
                        var file = Path.Combine(outDir, safe + ".dll");
                        File.WriteAllBytes(file, bytes);
                        Console.WriteLine($"  embedded dll {name} -> {file} ({bytes.Length})");
                    }
                }
            }
        }
    }
    return 0;
}

if (args[0] == "rewrite")
{
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(args.Length > 3 ? args[3] : "ref/deps");
    resolver.AddSearchDirectory(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319");
    resolver.AddSearchDirectory(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8");
    var rp = new ReaderParameters { InMemory = true, ReadSymbols = false, AssemblyResolver = resolver };
    using var asm = AssemblyDefinition.ReadAssembly(args[1], rp);
    try
    {
        var ad = resolver.Resolve(AssemblyNameReference.Parse("PCLCS, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"));
        Console.WriteLine("resolved: " + ad.MainModule.FileName);
    }
    catch (Exception ex) { Console.WriteLine("resolve failed: " + ex.Message); }
    asm.Write(args[2], new WriterParameters { WriteSymbols = false });
    Console.WriteLine($"Rewrote {args[1]} -> {args[2]}");
    return 0;
}

if (args[0] == "diff")
{
    static Dictionary<string, (string rva, string size, string hash)> Read(string path)
    {
        var d = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var p = line.Split('\t');
            if (p.Length < 5) continue;
            d[p[1]] = (p[2], p[3], p[4]);
        }
        return d;
    }
    var a = Read(args[1]);
    var b = Read(args[2]);
    Console.WriteLine($"A={args[1]} ({a.Count})  B={args[2]} ({b.Count})");
    foreach (var kv in a)
    {
        if (!b.TryGetValue(kv.Key, out var bv)) Console.WriteLine($"ONLY-A  {kv.Key}");
        else if (kv.Value.hash != bv.hash) Console.WriteLine($"CHANGED {kv.Key}  A:rva={kv.Value.rva} size={kv.Value.size}  B:rva={bv.rva} size={bv.size}");
    }
    foreach (var kv in b) if (!a.ContainsKey(kv.Key)) Console.WriteLine($"ONLY-B  {kv.Key}");
    return 0;
}

return 2;
