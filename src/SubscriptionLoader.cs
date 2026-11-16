using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using AncientAffectionCompat.Patching;
using MegaCrit.Sts2.Core.Modding;

namespace AncientAffectionCompat;

public static class SubscriptionLoader
{
    public static string? OriginalAssemblyPath { get; private set; }
    private static Assembly? _prepared;

    public static void Initialize()
    {
        var originals = ModManager.Mods.Where(m => m.manifest?.id == "AncientAffection").ToArray();
        if (originals.Length != 1 || originals[0].state != ModLoadState.None)
            throw new InvalidOperationException("Enable exactly one original Ancient Affection and place Ancient Affection Compatibility Patch ABOVE it in the mod list, then restart. 원본을 활성화하고 패치를 원본 위에 배치한 뒤 재시작하세요.");
        var gameAssemblies = Path.GetDirectoryName(typeof(ModManager).Assembly.Location)!;
        Prepare(Path.Combine(originals[0].path, "AncientAffection.dll"), gameAssemblies);
        CompatMod.Log("Original subscription prepared in memory. Waiting for the game's normal Ancient Affection initialization; no files changed.");
    }

    // Keep Cecil references in a separate call so the dependency is loaded before JIT.
    public static Assembly Prepare(string originalPath, string gameAssemblyDirectory)
    {
        var context = AssemblyLoadContext.GetLoadContext(typeof(SubscriptionLoader).Assembly)!;
        originalPath = Path.GetFullPath(originalPath);
        if (_prepared != null)
        {
            if (!string.Equals(OriginalAssemblyPath, originalPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A different Ancient Affection source was already prepared. Restart the game.");
            return _prepared;
        }
        if (context.Assemblies.Any(a => a.GetName().Name == "AncientAffection"))
            throw new InvalidOperationException("Ancient Affection loaded before its compatibility patch. Move this patch ABOVE Ancient Affection and restart. 패치를 원본 위로 옮긴 뒤 재시작하세요.");
        var gameDir = Directory.GetParent(Path.GetFullPath(gameAssemblyDirectory))!.FullName;
        using var release = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(gameDir, "release_info.json")));
        if (release.RootElement.GetProperty("version").GetString() != "v0.113.1")
            throw new InvalidOperationException("This compatibility patch supports game v0.113.1 only.");
        var ownDirectory = Path.GetDirectoryName(typeof(SubscriptionLoader).Assembly.Location)!;
        if (!context.Assemblies.Any(a => a.GetName().Name == "Mono.Cecil"))
            context.LoadFromAssemblyPath(Path.Combine(ownDirectory, "Mono.Cecil.dll"));
        if (!context.Assemblies.Any(a => a.GetName().Name == "AncientAffectionCompat.Patching"))
            context.LoadFromAssemblyPath(Path.Combine(ownDirectory, "AncientAffectionCompat.Patching.dll"));
        return RewriteAndLoad(context, originalPath, gameAssemblyDirectory);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly RewriteAndLoad(AssemblyLoadContext context, string originalPath, string gameAssemblyDirectory)
    {
        var result = AssemblyRewriter.Rewrite(File.ReadAllBytes(originalPath), gameAssemblyDirectory,
            typeof(SubscriptionLoader).Assembly.Location);
        using var bytes = new MemoryStream(result.AssemblyBytes, writable: false);
        var assembly = context.LoadFromStream(bytes);
        OriginalAssemblyPath = originalPath;
        _prepared = assembly;
        CompatMod.Log($"Prepared {result.ApiCalls} API calls, {result.RewardRngSites} reward RNG sites and {result.EligibilityHooks} eligibility hooks in memory.");
        return assembly;
    }
}
