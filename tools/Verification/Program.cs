using System.Reflection;
using System.Runtime.Loader;
using AncientAffectionCompat;
using HarmonyLib;
using Mono.Cecil;
using AncientAffectionCompat.Patching;

var game = Path.GetFullPath(args.Length > 0 ? args[0] : "..");
var workshop = Path.GetFullPath(Path.Combine(game, "../../workshop/content/2868840"));
var candidate = Path.GetFullPath(args.Length > 1 ? args[1] : "dist/workshop-content");
var paths = new[] { Path.Combine(game,"data_sts2_windows_x86_64"),
    Path.Combine(workshop,"3737335127/BaseLib"), Path.Combine(workshop,"3747602295/compat/0.113.0"),
    Path.Combine(workshop,"3747602295/shared"), Path.Combine(candidate,"AncientAffectionCompat"),
    System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory() };
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (var dir in paths) { var p = Path.Combine(dir,name.Name+".dll"); if(File.Exists(p)) return context.LoadFromAssemblyPath(p); }
    return null;
};
using var resolver = new DefaultAssemblyResolver();
foreach(var p in paths) resolver.AddSearchDirectory(p);
var originalDir = Path.GetFullPath(args.Length > 2 ? args[2] : Path.Combine(workshop,"3750930021"));
var dll = Path.Combine(originalDir,"AncientAffection.dll");
var originalBytes = File.ReadAllBytes(dll);
var result = AssemblyRewriter.Rewrite(originalBytes, paths[0], typeof(CompatMod).Assembly.Location);
using var moduleBytes = new MemoryStream(result.AssemblyBytes);
using var module = ModuleDefinition.ReadModule(moduleBytes,new ReaderParameters{AssemblyResolver=resolver});
var failures = new List<string>();
foreach(var member in module.GetMemberReferences().Where(m => m.DeclaringType.Scope.Name == "sts2"))
    try { if(member.Resolve()==null) failures.Add("Unresolved game API: "+member.FullName); }
    catch(Exception e) { failures.Add(member.FullName+": "+e.Message); }
foreach(var type in module.Types)
foreach(var method in type.Methods.Where(m=>m.IsVirtual && !m.IsNewSlot))
{
    var found = false;
    for(var b=type.BaseType?.Resolve(); b!=null; b=b.BaseType?.Resolve())
        if(b.Methods.Any(m=>m.Name==method.Name && m.IsVirtual && m.ReturnType.FullName==method.ReturnType.FullName && m.Parameters.Select(p=>p.ParameterType.FullName).SequenceEqual(method.Parameters.Select(p=>p.ParameterType.FullName)))) { found=true; break; }
    if(!found) failures.Add("Orphaned virtual override: "+method.FullName);
}
var prepared = SubscriptionLoader.Prepare(dll, paths[0]);
var ancient = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
if (!ReferenceEquals(prepared, ancient)) throw new Exception("Game load did not reuse the prepared original.");
if (ApiBridge.OriginalAssemblyLocation(ancient) != dll) throw new Exception("Original diagnostic path was lost.");
if (!File.ReadAllBytes(dll).SequenceEqual(originalBytes)) throw new Exception("Original DLL changed.");
Console.WriteLine("PASS: original path reuses memory assembly; diagnostic source path retained; original DLL unchanged.");
MegaCrit.Sts2.Core.TestSupport.TestMode.IsOn = true;
var types=ancient.GetTypes();
Console.WriteLine($"Loaded {types.Length} original types.");
var compiled = 0;
foreach (var type in types.Where(t => !t.ContainsGenericParameters))
foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
    .Where(m => !m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null
        && !(m.DeclaringType?.Name == "MerchantFavorMinigame" && new[] { "InvokeGodotClassMethod", "HasGodotClassMethod", "SetGodotClassPropertyValue", "GetGodotClassPropertyValue", "SaveGodotObjectData", "RestoreGodotObjectData" }.Contains(m.Name))))
{
    try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle); compiled++; }
    catch(Exception e) { failures.Add($"JIT {type.Name}.{method.Name}: {e}"); }
}
Console.WriteLine($"JIT compiled {compiled} original methods.");
new Harmony("AncientAffection.harmony").PatchAll(ancient);
CompatMod.InitializeAfterAncient();
Console.WriteLine($"Installed {Harmony.GetAllPatchedMethods().Count(m=>Harmony.GetPatchInfo(m)?.Owners.Contains("Nayuri.AncientAffectionCompat")==true)} compatibility patches.");
await BehaviorChecks.Run(ancient);
foreach(var f in failures) Console.Error.WriteLine(f);
if(failures.Count>0) Environment.Exit(1);
Console.WriteLine("PASS: game member references, virtual overrides, type load, runtime Harmony installation.");
