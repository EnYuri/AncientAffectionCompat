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
LoaderTestEnvironment.Initialize(sts2);
var manager = sts2.GetType("MegaCrit.Sts2.Core.Modding.ModManager", true)!;
var modType = sts2.GetType("MegaCrit.Sts2.Core.Modding.Mod", true)!;
var manifestType = sts2.GetType("MegaCrit.Sts2.Core.Modding.ModManifest", true)!;
var stateType = sts2.GetType("MegaCrit.Sts2.Core.Modding.ModLoadState", true)!;
object Mod(string id, string path, string state = "None") {
    var manifest = Activator.CreateInstance(manifestType)!;
    var manifestPath = Path.Combine(path, id + ".json");
    if (File.Exists(manifestPath)) {
        using var stream = File.OpenRead(manifestPath);
        manifest = manifestType.GetMethod("ReadFromStream")!.Invoke(null, new object?[] { stream, null })!;
    } else manifestType.GetField("id")!.SetValue(manifest, id);
    var mod = Activator.CreateInstance(modType)!;
    modType.GetField("manifest")!.SetValue(mod, manifest);
    modType.GetField("path")!.SetValue(mod, path);
    modType.GetField("state")!.SetValue(mod, Enum.Parse(stateType, state));
    return mod;
}
var listType = typeof(List<>).MakeGenericType(modType);
var mods = (IList)Activator.CreateInstance(listType)!;
mods.Add(Mod("AncientAffectionCompat", Path.GetDirectoryName(compatPath)!));
if (scenario != "missing") mods.Add(Mod("AncientAffection", originalDir, scenario == "disabled" ? "Disabled" : "Loaded"));
if (scenario == "duplicate") mods.Add(Mod("AncientAffection", originalDir, "Loaded"));
manager.GetField("_mods", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, mods);
var loader = compat.GetType("AncientAffectionCompat.SubscriptionLoader", true)!;
var apply = loader.GetMethod("Apply")!;
if (scenario == "ordering") {
    var all = new[] {
        Mod("AncientAffectionCompat", Path.GetDirectoryName(compatPath)!),
        Mod("AncientAffection", originalDir),
        Mod("BaseLib", Path.Combine(workshop, "3737335127/BaseLib")),
        Mod("BaseLibCompatibilityHelper", Path.Combine(workshop, "3737335127/BaseLib")),
        Mod("STS2-RitsuLib", Path.Combine(workshop, "3747602295"))
    };
    var settingsType = sts2.GetType("MegaCrit.Sts2.Core.Modding.SettingsSaveMod", true)!;
    var orderingType = typeof(List<>).MakeGenericType(settingsType);
    string Id(object mod) => (string)manifestType.GetField("id")!.GetValue(modType.GetField("manifest")!.GetValue(mod))!;
    IEnumerable<object[]> Permutations(object[] remaining) {
        if (remaining.Length == 0) { yield return []; yield break; }
        for (var i = 0; i < remaining.Length; i++)
            foreach (var tail in Permutations(remaining.Where((_, index) => i != index).ToArray()))
                yield return new[] { remaining[i] }.Concat(tail).ToArray();
    }
    var checkedOrders = 0;
    foreach (var order in Permutations(all)) {
        var detected = (IList)Activator.CreateInstance(listType)!;
        // Discovery and user ordering differ, as with existing subscriptions plus a new patch.
        foreach (var mod in order.Reverse()) detected.Add(mod);
        manager.GetField("_mods", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, detected);
        var userOrder = (IList)Activator.CreateInstance(orderingType)!;
        foreach (var mod in order) userOrder.Add(Activator.CreateInstance(settingsType, mod));
        manager.GetMethod("SortModList", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [userOrder]);
        var sorted = ((IEnumerable)manager.GetProperty("Mods")!.GetValue(null)!).Cast<object>().Select(Id).ToList();
        foreach (var (dependency, dependent) in new[] { ("BaseLibCompatibilityHelper", "BaseLib"), ("BaseLib", "AncientAffection"),
            ("STS2-RitsuLib", "AncientAffection"), ("AncientAffection", "AncientAffectionCompat") })
            if (sorted.IndexOf(dependency) >= sorted.IndexOf(dependent)) throw new Exception("Incorrect dependency order: " + string.Join(",", sorted));
        checkedOrders++;
    }
    Console.WriteLine($"PASS: game's real dependency sorter handles all {checkedOrders} manual orders without user rearrangement.");
    return;
}
var ancient = context.LoadFromAssemblyPath(source);
// Reproduce original Harmony registration before the add-on starts, without Godot UI or saves.
var harmonyType = context.LoadFromAssemblyPath(Path.Combine(gameAssemblies, "0Harmony.dll")).GetType("HarmonyLib.Harmony", true)!;
var harmony = Activator.CreateInstance(harmonyType, "AncientAffection.harmony")!;
harmonyType.GetMethod("PatchAll", new[] { typeof(Assembly) })!.Invoke(harmony, [ancient]);
var expected = scenario switch {
    "missing" or "disabled" or "duplicate" => "Enable exactly one original",
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
            apply.Invoke(null, new object[] { changedPath, gameAssemblies });
        } else {
            var fakeAssemblies = Path.Combine(fixture, "data_sts2_windows_x86_64");
            Directory.CreateDirectory(fakeAssemblies);
            File.WriteAllText(Path.Combine(fixture, "release_info.json"), "{\"version\":\"v0.999.0\"}");
            apply.Invoke(null, new object[] { source, fakeAssemblies });
        }
    } else initializer.GetMethod("Initialize")!.Invoke(null, null);
    if (expected != null) throw new Exception("Expected rejection: " + scenario);
} catch (TargetInvocationException e) when (expected != null && e.InnerException is InvalidOperationException && e.InnerException.Message.Contains(expected)) {
    Console.WriteLine("PASS: packaged loader rejects " + scenario + " with actionable error; source unchanged.");
    if (!sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source)))) throw new Exception("Source changed.");
    return;
}
if (!ReferenceEquals(ancient, apply.Invoke(null, new object[] { source, gameAssemblies })))
    throw new Exception("Add-on replaced the original assembly identity.");
if (ancient.GetTypes().Length != 614) throw new Exception("Unexpected original type count.");
var signature = ancient.GetType("AncientAffection.AncientAffectionDiagnostics", true)!
    .GetMethod("CreateLocalSignature", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
if ((string?)signature.GetType().GetProperty("Directory")!.GetValue(signature) != originalDir)
    throw new Exception("Original diagnostics lost their subscribed source directory.");
if (context.Assemblies.Count(a => a.GetName().Name == "AncientAffection") != 1)
    throw new Exception("Original was loaded twice.");
if (!sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source)))) throw new Exception("Source changed.");
Console.WriteLine("PASS: packaged add-on applies after original initialization, resolves its own helpers, preserves 614 original types and leaves source untouched.");

static class LoaderTestEnvironment
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void Initialize(Assembly game)
    {
        var harmony = new HarmonyLib.Harmony("compat.loader-verification");
        harmony.Patch(HarmonyLib.AccessTools.Method(game.GetType("MegaCrit.Sts2.Core.Logging.Logger"), "GetIsRunningFromGodotEditor"),
            transpiler: new HarmonyLib.HarmonyMethod(typeof(LoaderTestEnvironment), nameof(NoEditor)));
        harmony.Patch(HarmonyLib.AccessTools.Method(game.GetType("MegaCrit.Sts2.Core.Logging.ConsoleLogPrinter"), "Print"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(LoaderTestEnvironment), nameof(Quiet)));
    }
    static IEnumerable<HarmonyLib.CodeInstruction> NoEditor(IEnumerable<HarmonyLib.CodeInstruction> instructions) =>
        [new(System.Reflection.Emit.OpCodes.Ldc_I4_0), new(System.Reflection.Emit.OpCodes.Ret)];
    static bool Quiet() => false;
}
