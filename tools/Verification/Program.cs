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
// Production path: the original assembly and its Harmony registrations precede the add-on.
var ancient = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
MegaCrit.Sts2.Core.TestSupport.TestMode.IsOn = true;
new Harmony("AncientAffection.harmony").PatchAll(ancient);
var applied = SubscriptionLoader.Apply(dll, paths[0]);
if (!ReferenceEquals(applied, ancient) || ancient.Location != dll) throw new Exception("Original assembly identity or location changed.");
if (!File.ReadAllBytes(dll).SequenceEqual(originalBytes)) throw new Exception("Original DLL changed.");
Console.WriteLine("PASS: runtime patch preserves the already-loaded original assembly and its source file.");
var types=ancient.GetTypes();
Console.WriteLine($"Loaded {types.Length} original types.");
var compiled = 0;
foreach (var type in types.Where(t => !t.ContainsGenericParameters))
foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
    .Where(m => !m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null
        && !(m.DeclaringType?.Name == "MerchantFavorMinigame" && new[] { "InvokeGodotClassMethod", "HasGodotClassMethod", "SetGodotClassPropertyValue", "GetGodotClassPropertyValue", "SaveGodotObjectData", "RestoreGodotObjectData" }.Contains(m.Name))))
{
    try {
        if (RuntimeApiPatches.HasUnavailableOriginalBody(method)) continue;
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle); compiled++;
    }
    catch(Exception e) { failures.Add($"JIT {type.Name}.{method.Name}: {e}"); }
}
Console.WriteLine($"JIT compiled {compiled} original methods.");
Console.WriteLine($"Installed {Harmony.GetAllPatchedMethods().Count(m=>Harmony.GetPatchInfo(m)?.Owners.Contains("Nayuri.AncientAffectionCompat")==true)} compatibility patches.");
var brokenTokens = result.Plan.ApiCalls.Select(c => c.MethodToken).ToHashSet();
foreach (var token in result.Plan.BrokenCallers)
{
    var caller = ancient.ManifestModule.ResolveMethod(token)!;
    if (RuntimeApiPatches.HasUnavailableOriginalBody(caller)) continue;
    foreach (var instruction in PatchProcessor.GetCurrentInstructions(caller))
        if (instruction.operand is MethodInfo called && called.Module == ancient.ManifestModule && brokenTokens.Contains(called.MetadataToken))
            failures.Add("Live caller still references an unavailable body: " + caller + " -> " + called);
}
Console.WriteLine("Checked all original call sites that referenced incompatible method bodies.");
await BehaviorChecks.Run(ancient);
foreach(var f in failures) Console.Error.WriteLine(f);
if(failures.Count>0) Environment.Exit(1);
Console.WriteLine("PASS: API bridges, original type identity, runtime patch installation and behavior checks.");
