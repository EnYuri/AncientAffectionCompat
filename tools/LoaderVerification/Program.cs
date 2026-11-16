using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

var game = Path.GetFullPath(args[0]);
var package = Path.GetFullPath(args[1]);
var originalDir = Path.GetFullPath(args[2]);
var scenario = args.Length > 3 ? args[3] : "normal";
var workshop = Path.GetFullPath(Path.Combine(game, "../../workshop/content/2868840"));
var gameAssemblies = Path.Combine(game, "data_sts2_windows_x86_64");
var search = new[] { gameAssemblies, Path.Combine(workshop, "3737335127/BaseLib"),
    Path.Combine(workshop, "3747602295/compat/0.113.0"), Path.Combine(workshop, "3747602295/shared") };
var context = AssemblyLoadContext.Default;
context.Resolving += (c, name) => {
    foreach (var dir in search) {
        var file = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(file)) return c.LoadFromAssemblyPath(file);
    }
    return null;
};
var source = Path.Combine(originalDir, "AncientAffection.dll");
var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
var compatPath = Path.Combine(package, "AncientAffectionCompat/AncientAffectionCompat.dll");
var compat = context.LoadFromAssemblyPath(compatPath);
// Reproduce the loader's discovery BEFORE initialization, with no automatic resolver
// for the patch's helper DLLs. The mod must load its own private dependencies.
var types = compat.GetTypes();
var initializer = types.Single(t => t.FullName == "AncientAffectionCompat.CompatMod");
if (context.Assemblies.Any(a => a.GetName().Name is "Mono.Cecil" or "AncientAffectionCompat.Patching"))
    throw new Exception("Smoke test accidentally preloaded a patch dependency.");
var sts2 = context.LoadFromAssemblyPath(Path.Combine(gameAssemblies, "sts2.dll"));
var manager = sts2.GetType("MegaCrit.Sts2.Core.Modding.ModManager", true)!;
var modType = sts2.GetType("MegaCrit.Sts2.Core.Modding.Mod", true)!;
var manifestType = sts2.GetType("MegaCrit.Sts2.Core.Modding.ModManifest", true)!;
var stateType = sts2.GetType("MegaCrit.Sts2.Core.Modding.ModLoadState", true)!;
object Mod(string id, string path, string state = "None") {
    var manifest = Activator.CreateInstance(manifestType)!;
    manifestType.GetField("id")!.SetValue(manifest, id);
    var mod = Activator.CreateInstance(modType)!;
    modType.GetField("manifest")!.SetValue(mod, manifest);
    modType.GetField("path")!.SetValue(mod, path);
    modType.GetField("state")!.SetValue(mod, Enum.Parse(stateType, state));
    return mod;
}
var mods = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(modType))!;
mods.Add(Mod("AncientAffectionCompat", Path.GetDirectoryName(compatPath)!));
if (scenario != "missing") mods.Add(Mod("AncientAffection", originalDir, scenario == "disabled" ? "Disabled" : "None"));
if (scenario == "duplicate") mods.Add(Mod("AncientAffection", originalDir));
manager.GetField("_mods", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, mods);
if (scenario == "late") context.LoadFromAssemblyPath(source);
var loader = compat.GetType("AncientAffectionCompat.SubscriptionLoader", true)!;
var prepare = loader.GetMethod("Prepare")!;
var expected = scenario switch {
    "missing" or "disabled" or "duplicate" => "Enable exactly one original",
    "late" => "loaded before its compatibility patch",
    "modified" => "Unsupported Ancient Affection DLL",
    "wrong-game" => "supports game v0.113.1 only",
    _ => null
};
try {
    if (scenario is "modified" or "wrong-game") {
        // All fixture writes are inside ignored build output, never installed mods.
        var fixture = Path.Combine(Path.GetDirectoryName(package)!, "loader-fixtures", scenario);
        Directory.CreateDirectory(fixture);
        if (scenario == "modified") {
            var changed = File.ReadAllBytes(source);
            changed[^1] ^= 1;
            var changedPath = Path.Combine(fixture, "AncientAffection.dll");
            File.WriteAllBytes(changedPath, changed);
            prepare.Invoke(null, new object[] { changedPath, gameAssemblies });
        } else {
            var fakeAssemblies = Path.Combine(fixture, "data_sts2_windows_x86_64");
            Directory.CreateDirectory(fakeAssemblies);
            File.WriteAllText(Path.Combine(fixture, "release_info.json"), "{\"version\":\"v0.999.0\"}");
            prepare.Invoke(null, new object[] { source, fakeAssemblies });
        }
    } else initializer.GetMethod("Initialize")!.Invoke(null, null);
    if (expected != null) throw new Exception("Expected rejection: " + scenario);
} catch (TargetInvocationException e) when (expected != null && e.InnerException is InvalidOperationException && e.InnerException.Message.Contains(expected)) {
    if (scenario != "late" && context.Assemblies.Any(a => a.GetName().Name == "AncientAffection"))
        throw new Exception("Invalid input loaded Ancient Affection before rejecting it.");
    Console.WriteLine("PASS: packaged loader rejects " + scenario + " with actionable error; source unchanged.");
    if (!sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source)))) throw new Exception("Source changed.");
    return;
}
var ancient = context.LoadFromAssemblyPath(source);
if (!ReferenceEquals(ancient, prepare.Invoke(null, new object[] { source, gameAssemblies })))
    throw new Exception("Normal game path did not reuse the in-memory assembly.");
if (ancient.GetTypes().Length != 614) throw new Exception("Unexpected original type count.");
var signature = ancient.GetType("AncientAffection.AncientAffectionDiagnostics", true)!
    .GetMethod("CreateLocalSignature", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
if ((string?)signature.GetType().GetProperty("Directory")!.GetValue(signature) != originalDir)
    throw new Exception("Original diagnostics lost their subscribed source directory.");
if (context.Assemblies.Count(a => a.GetName().Name == "AncientAffection") != 1)
    throw new Exception("Original was loaded twice.");
if (!sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source)))) throw new Exception("Source changed.");
Console.WriteLine("PASS: packaged initializer discovers and prepares original, resolves its own helpers, loads 614 types once and leaves original untouched.");
