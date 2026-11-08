using System.Collections;
using System.Reflection;
using System.Runtime.Loader;

string sourcePath = args[0];
string outputPath = args[1];
string shokoBuildDir = "/usr/src/app/build";

AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    string candidate = Path.Combine(shokoBuildDir, name.Name + ".dll");
    return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
};

static object? GetDefaultArg(ParameterInfo p)
{
    if (p.HasDefaultValue)
        return Type.Missing;
    return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
}

var caAsm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(shokoBuildDir, "Microsoft.CodeAnalysis.dll"));
var csAsm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(shokoBuildDir, "Microsoft.CodeAnalysis.CSharp.dll"));

var syntaxTreeType = csAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree")!;
var parseTextMethod = syntaxTreeType.GetMethods(BindingFlags.Public | BindingFlags.Static)
    .First(m => m.Name == "ParseText" && m.GetParameters()[0].ParameterType == typeof(string));

string code = File.ReadAllText(sourcePath);
var par = parseTextMethod.GetParameters();
object?[] parseArgs = new object?[par.Length];
parseArgs[0] = code;
for (int i = 1; i < par.Length; i++) parseArgs[i] = GetDefaultArg(par[i]);
var tree = parseTextMethod.Invoke(null, parseArgs)!;

var syntaxTreeBaseType = caAsm.GetType("Microsoft.CodeAnalysis.SyntaxTree")!;
var treesArray = Array.CreateInstance(syntaxTreeBaseType, 1);
treesArray.SetValue(tree, 0);

var metaRefType = caAsm.GetType("Microsoft.CodeAnalysis.MetadataReference")!;
var createFromFileMethod = metaRefType.GetMethods(BindingFlags.Public | BindingFlags.Static)
    .First(m => m.Name == "CreateFromFile" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(string));
var cffParams = createFromFileMethod.GetParameters();

var refDirs = new List<string> { shokoBuildDir };
string netCoreDir = "/usr/share/dotnet/shared/Microsoft.NETCore.App";
if (Directory.Exists(netCoreDir))
    refDirs.AddRange(Directory.GetDirectories(netCoreDir));
string aspNetDir = "/usr/share/dotnet/shared/Microsoft.AspNetCore.App";
if (Directory.Exists(aspNetDir))
    refDirs.AddRange(Directory.GetDirectories(aspNetDir));

var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var refsList = new List<object>();

foreach (var dir in refDirs)
{
    foreach (var dll in Directory.GetFiles(dir, "*.dll"))
    {
        string fn = Path.GetFileName(dll);
        if (fn.Contains("Native") || fn.StartsWith("lib") || !seenNames.Add(fn))
            continue;
        try
        {
            var r = createFromFileMethod.Invoke(null, new object?[] { dll, GetDefaultArg(cffParams[1]), GetDefaultArg(cffParams[2]) });
            if (r != null) refsList.Add(r);
        }
        catch
        {
            // skip non-managed dlls
        }
    }
}

var refsArray = Array.CreateInstance(metaRefType, refsList.Count);
for (int i = 0; i < refsList.Count; i++) refsArray.SetValue(refsList[i], i);

var outputKindType = caAsm.GetType("Microsoft.CodeAnalysis.OutputKind")!;
var dllKind = Enum.ToObject(outputKindType, 2); // DynamicallyLinkedLibrary = 2
var nullableContextOptionsType = caAsm.GetType("Microsoft.CodeAnalysis.NullableContextOptions")!;
var nullableEnable = Enum.ToObject(nullableContextOptionsType, 3); // Enable = 3

var compOptsType = csAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions")!;
var ctor = compOptsType.GetConstructors().First(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == outputKindType);
var ctorParams = ctor.GetParameters();
object?[] ctorArgs = new object?[ctorParams.Length];
ctorArgs[0] = dllKind;
for (int i = 1; i < ctorParams.Length; i++)
{
    if (ctorParams[i].Name == "nullableContextOptions")
        ctorArgs[i] = nullableEnable;
    else
        ctorArgs[i] = GetDefaultArg(ctorParams[i]);
}
var compOpts = ctor.Invoke(ctorArgs);

var csCompType = csAsm.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation")!;
var createCompMethod = csCompType.GetMethods(BindingFlags.Public | BindingFlags.Static)
    .First(m => m.Name == "Create" && m.GetParameters().Length == 4);

var compilation = createCompMethod.Invoke(null, new object?[]
{
    Path.GetFileNameWithoutExtension(outputPath),
    treesArray,
    refsArray,
    compOpts
})!;

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
using var fs = File.Create(outputPath);
var emitMethod = csCompType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
    .First(m => m.Name == "Emit" && m.GetParameters()[0].ParameterType == typeof(Stream));
var emitParams = emitMethod.GetParameters();
object?[] emitArgs = new object?[emitParams.Length];
emitArgs[0] = fs;
for (int i = 1; i < emitParams.Length; i++) emitArgs[i] = GetDefaultArg(emitParams[i]);

var emitResult = emitMethod.Invoke(compilation, emitArgs)!;
var success = (bool)emitResult.GetType().GetProperty("Success")!.GetValue(emitResult)!;
var diagnostics = (IEnumerable)emitResult.GetType().GetProperty("Diagnostics")!.GetValue(emitResult)!;

foreach (var diag in diagnostics)
{
    var sev = diag.GetType().GetProperty("Severity")!.GetValue(diag)!.ToString();
    if (sev == "Error" || sev == "Warning")
        Console.WriteLine(diag.ToString());
}

if (!success)
{
    Console.Error.WriteLine("Compilation FAILED.");
    Environment.Exit(1);
}

Console.WriteLine($"Successfully compiled -> {outputPath}");
