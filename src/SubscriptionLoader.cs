using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using AncientAffectionCompat.Patching;
using MegaCrit.Sts2.Core.Modding;

namespace AncientAffectionCompat;

public static class SubscriptionLoader
{
    private static Assembly? _applied;

    public static void Initialize()
    {
        var originals = ModManager.Mods.Where(m => m.manifest?.id == "AncientAffection").ToArray();
        if (originals.Length != 1 || originals[0].state != ModLoadState.Loaded || originals[0].errors?.Count > 0)
            throw new InvalidOperationException("Enable exactly one original Ancient Affection with BaseLib and RitsuLib. The game's dependency loader loads it before this patch automatically. 원본과 필수 모드를 활성화하세요. 로딩 순서는 자동으로 정렬됩니다.");
        Apply(Path.Combine(originals[0].path, "AncientAffection.dll"), Path.GetDirectoryName(typeof(ModManager).Assembly.Location)!);
        CompatMod.Log("Original subscription patched after initialization; dependency order is automatic, no files changed.");
    }

    // Resolve helper assemblies before JIT-compiling methods that reference them.
    public static Assembly Apply(string originalPath, string gameAssemblyDirectory)
    {
        var context = AssemblyLoadContext.GetLoadContext(typeof(SubscriptionLoader).Assembly)!;
        originalPath = Path.GetFullPath(originalPath);
        if (_applied != null)
        {
            if (!string.Equals(_applied.Location, originalPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A different Ancient Affection source was already patched. Restart the game.");
            return _applied;
        }
        var gameDir = Directory.GetParent(Path.GetFullPath(gameAssemblyDirectory))!.FullName;
        using var release = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(gameDir, "release_info.json")));
        if (release.RootElement.GetProperty("version").GetString() != "v0.113.1")
            throw new InvalidOperationException("This compatibility patch supports game v0.113.1 only.");
        var ownDirectory = Path.GetDirectoryName(typeof(SubscriptionLoader).Assembly.Location)!;
        if (!context.Assemblies.Any(a => a.GetName().Name == "Mono.Cecil"))
            context.LoadFromAssemblyPath(Path.Combine(ownDirectory, "Mono.Cecil.dll"));
        if (!context.Assemblies.Any(a => a.GetName().Name == "AncientAffectionCompat.Patching"))
            context.LoadFromAssemblyPath(Path.Combine(ownDirectory, "AncientAffectionCompat.Patching.dll"));
        return ApplyRuntime(context, originalPath, gameAssemblyDirectory);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly ApplyRuntime(AssemblyLoadContext context, string originalPath, string gameAssemblyDirectory)
    {
        var result = AssemblyRewriter.Rewrite(File.ReadAllBytes(originalPath), gameAssemblyDirectory,
            typeof(SubscriptionLoader).Assembly.Location);
        var ancient = context.Assemblies.SingleOrDefault(a => a.GetName().Name == "AncientAffection")
            ?? throw new InvalidOperationException("The original Ancient Affection dependency has not loaded.");
        if (!string.Equals(Path.GetFullPath(ancient.Location), originalPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Loaded Ancient Affection does not match the subscribed source.");
        RuntimeApiPatches.Install(ancient, result);
        CompatMod.InitializeAfterAncient();
        _applied = ancient;
        CompatMod.Log($"Applied {result.ApiCalls} API calls, {result.RewardRngSites} reward RNG sites and {result.EligibilityHooks} eligibility hooks after original load.");
        return ancient;
    }
}
