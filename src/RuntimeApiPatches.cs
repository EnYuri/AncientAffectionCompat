using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using AncientAffectionCompat.Patching;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AncientAffectionCompat;

public static class RuntimeApiPatches
{
    private static HashSet<MethodBase> _unavailableBodies = [];
    private static Dictionary<MethodBase, MethodInfo> _copies = [];
    private static Dictionary<MethodBase, MethodInfo> _synchronousCopies = [];
    private static Dictionary<MethodBase, int> _rngCounts = [];
    private static Dictionary<Type, MethodInfo> _eligibility = [];
    private static Type _hellType = null!;
    private static MethodInfo _hellReward = null!;
    private static bool _installed;

    public static bool HasUnavailableOriginalBody(MethodBase method) => _unavailableBodies.Contains(method);
    public static Type EffectiveStateMachine(MethodInfo outer) => _copies.TryGetValue(outer, out var copy)
        ? copy.GetMethodBody()!.LocalVariables.Single(v => typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(v.LocalType)).LocalType
        : outer.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()!.StateMachineType;
    public static object? Invoke(MethodInfo method, object? instance, object?[] args) =>
        _synchronousCopies.TryGetValue(method, out var copy)
            ? copy.Invoke(null, method.IsStatic ? args : new[] { instance }.Concat(args).ToArray())
            : method.Invoke(instance, args);

    public static void Install(Assembly ancient, RewriteResult result)
    {
        if (_installed) return;
        var source = ancient.ManifestModule;
        var plan = result.Plan;
        var copies = AsyncCopyBuilder.Build(result.AssemblyBytes, plan);
        using var copyBytes = new MemoryStream(copies.AssemblyBytes);
        var copyAssembly = AssemblyLoadContext.GetLoadContext(ancient)!.LoadFromStream(copyBytes);
        var host = copyAssembly.GetType("AncientAffectionCompat.Generated.AsyncMethods", true)!;
        foreach (var type in copyAssembly.GetTypes())
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            if (method.GetMethodBody() != null) System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
        _copies = copies.OuterMethods.ToDictionary(p => source.ResolveMethod(p.Key)!, p => host.GetMethod(p.Value)!);
        _synchronousCopies = _copies.Where(p => copies.SynchronousMethods.Contains(p.Key.MetadataToken)).ToDictionary();
        _unavailableBodies = plan.ApiCalls.Select(c => source.ResolveMethod(c.MethodToken)!).ToHashSet();
        if (_synchronousCopies.Count != 7 || _copies.Count != 18)
            throw new InvalidOperationException("Unexpected synchronous/async API patch layout.");
        _rngCounts = plan.RewardRngCounts.Where(p => !copies.MoveNextMethods.Contains(p.Key) && !copies.SynchronousMethods.Contains(p.Key))
            .ToDictionary(p => source.ResolveMethod(p.Key)!, p => p.Value);
        var harmony = new Harmony(CompatMod.HarmonyId);
        // Patch valid callers, never compile or detour an invalid original method body.
        foreach (var method in plan.BrokenCallers.Select(t => source.ResolveMethod(t)!).Union(_rngCounts.Keys)
            .Where(m => !_unavailableBodies.Contains(m) && !_copies.ContainsKey(m)))
            harmony.Patch(method, transpiler: Patch(nameof(RewriteCalls)));
        foreach (var method in _copies.Keys.Where(m => !_synchronousCopies.ContainsKey(m)))
            harmony.Patch(method, transpiler: Patch(nameof(ReplaceAsyncMethod)));
        var replacedRegistrations = 0;
        foreach (var target in Harmony.GetAllPatchedMethods().ToArray())
        {
            var info = Harmony.GetPatchInfo(target);
            foreach (var registration in info.Prefixes.Where(p => _synchronousCopies.ContainsKey(p.PatchMethod)).ToArray())
            {
                var replacement = new HarmonyMethod(_synchronousCopies[registration.PatchMethod])
                { priority = registration.priority, before = registration.before, after = registration.after };
                harmony.Unpatch(target, registration.PatchMethod);
                harmony.Patch(target, prefix: replacement);
                replacedRegistrations++;
            }
        }
        if (replacedRegistrations != 1) throw new InvalidOperationException("Expected original merchant prefix registration.");
        _hellType = ancient.GetType("AncientAffection.HellNeowAidRelic", true)!;
        _hellReward = _synchronousCopies[AccessTools.Method(_hellType, "TryModifyRewards")];
        harmony.Patch(AccessTools.Method(typeof(Hook), "ModifyRewards"), transpiler: Patch(nameof(RewardDispatch)));
        foreach (var name in new[] { "ImprovedSilverCrucible", "ImprovedWingedBoots", "ImprovedMassiveScroll" })
        {
            var type = ancient.GetType("AncientAffection." + name, true)!;
            _eligibility.Add(type, type.GetMethod("IsAllowed", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!);
        }
        harmony.Patch(AccessTools.Method(typeof(RelicModel), "IsAllowedInternal"), prefix: Patch(nameof(Eligibility)));
        _installed = true;
    }

    private static HarmonyMethod Patch(string name) => new(typeof(RuntimeApiPatches), name);
    private static IEnumerable<CodeInstruction> RewriteCalls(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var codes = instructions.ToList();
        foreach (var code in codes)
            if (code.operand is MethodInfo method && _synchronousCopies.TryGetValue(method, out var copy))
            {
                if (code.opcode != OpCodes.Call && code.opcode != OpCodes.Callvirt && code.opcode != OpCodes.Ldftn)
                    throw new InvalidOperationException("Unsupported copied-method dispatch: " + code.opcode);
                if (code.opcode == OpCodes.Callvirt) code.opcode = OpCodes.Call;
                code.operand = copy;
            }
        var count = 0;
        for (var i = 0; i + 2 < codes.Count; i++)
        {
            if (codes[i].operand is not MethodInfo { Name: "get_RunState", DeclaringType.Name: "Player" } ||
                codes[i + 1].operand is not MethodInfo { Name: "get_Rng" } ||
                codes[i + 2].operand is not MethodInfo rng || rng.DeclaringType?.Name != "RunRngSet" ||
                rng.Name is not ("get_Niche" or "get_CombatPotionGeneration")) continue;
            if (!_rngCounts.ContainsKey(__originalMethod)) continue;
            codes[i].opcode = OpCodes.Call;
            codes[i].operand = AccessTools.Method(typeof(ApiBridge), nameof(ApiBridge.RewardRng));
            codes[i + 1].opcode = codes[i + 2].opcode = OpCodes.Nop;
            codes[i + 1].operand = codes[i + 2].operand = null;
            count++;
        }
        if (count != _rngCounts.GetValueOrDefault(__originalMethod))
            throw new InvalidOperationException("Unexpected reward RNG code in " + __originalMethod);
        return codes;
    }

    private static IEnumerable<CodeInstruction> ReplaceAsyncMethod(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var count = __originalMethod.GetParameters().Length + (__originalMethod.IsStatic ? 0 : 1);
        for (var i = 0; i < count; i++) yield return new CodeInstruction(OpCodes.Ldarg, (short)i);
        yield return new CodeInstruction(OpCodes.Call, _copies[__originalMethod]);
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static IEnumerable<CodeInstruction> RewardDispatch(IEnumerable<CodeInstruction> instructions)
    {
        var target = AccessTools.Method(typeof(AbstractModel), "TryModifyRewards");
        var count = 0;
        foreach (var code in instructions)
        {
            if (code.Calls(target))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(RuntimeApiPatches), nameof(ModifyRewards));
                count++;
            }
            yield return code;
        }
        if (count != 1) throw new InvalidOperationException("Unexpected game reward dispatch code.");
    }
    private static bool ModifyRewards(AbstractModel model, Player player, List<Reward> rewards, AbstractRoom? room) =>
        _hellType.IsInstanceOfType(model)
            ? (bool)_hellReward.Invoke(null, [model, player, rewards, room])!
            : model.TryModifyRewards(player, rewards, room);

    private static bool Eligibility(RelicModel __instance, IRunState __0, ref bool __result)
    {
        foreach (var (type, method) in _eligibility)
            if (type.IsInstanceOfType(__instance))
            {
                __result = (bool)method.Invoke(__instance, [__0])!;
                return false;
            }
        return true;
    }
}
