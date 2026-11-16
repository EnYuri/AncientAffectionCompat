using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AncientAffectionCompat.Patching;

public sealed record ApiCallSite(int MethodToken, int MemberToken, string BridgeName, string MethodName);
public sealed record RuntimePatchPlan(ApiCallSite[] ApiCalls, Dictionary<int, int> RewardRngCounts, Dictionary<string, int> OriginalMethodTokens, int[] BrokenCallers);
public sealed record RewriteResult(byte[] AssemblyBytes, int ApiCalls, int RewardRngSites, int EligibilityHooks, RuntimePatchPlan Plan);

public static class AssemblyRewriter
{
    public const string SupportedOriginalHash = "20FB2DCEEA8D83DB5596044EB09F9C3AA9B728B32A71C9FB112E4232561FC085";
    public const string SupportedGameVersion = "v0.113.1";

    public static RewriteResult Rewrite(byte[] originalBytes, string gameAssemblyDirectory, string bridgePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(originalBytes));
        if (hash != SupportedOriginalHash)
            throw new InvalidOperationException("Unsupported Ancient Affection DLL. This patch requires the reviewed v0.1.0 original: " + hash);
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(gameAssemblyDirectory);
        resolver.AddSearchDirectory(Path.GetDirectoryName(bridgePath)!);
        using var input = new MemoryStream(originalBytes, writable: false);
        using var original = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver });
        using var bridge = ModuleDefinition.ReadModule(bridgePath, new ReaderParameters { AssemblyResolver = resolver });
        var api = bridge.GetType("AncientAffectionCompat.ApiBridge");
        var originalMethodTokens = AllTypes(original.Types).SelectMany(t => t.Methods)
            .ToDictionary(m => m.FullName, m => m.MetadataToken.ToInt32());
        var edits = new List<ApiCallSite>();
        var unresolved = new HashSet<string>();
        foreach (var type in AllTypes(original.Types))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference member || member.DeclaringType.Scope.Name != "sts2") continue;
            if (member.Resolve() != null) continue;
            var name = (member.DeclaringType.Name, member.Name, member.Parameters.Count) switch
            {
                ("MerchantInventory", "get_AllEntries", 0) => "AllEntries",
                ("RelicReward", ".ctor", 2) => "RelicReward",
                ("CardReward", ".ctor", 4) => "GeneratedCardReward",
                ("CardReward", ".ctor", 5) => "OfferedCardReward",
                ("CreatureCmd", "LoseMaxHp", 4) => "LoseMaxHp",
                ("CardSelectCmd", "FromChooseABundleScreen", 2) => "ChooseBundle",
                ("CardCmd", "Exhaust", 4) => "Exhaust",
                ("MerchantEntry", "OnTryPurchaseWrapper", 2) => "Purchase",
                ("MerchantCardRemovalEntry", "OnTryPurchaseWrapper", 3) => "RemoveCard",
                _ => null
            };
            if (name == null) { unresolved.Add(member.FullName); continue; }
            var replacement = api.Methods.Single(m => m.Name == name);
            var expected = member.Parameters.Select(p => p.ParameterType.FullName).ToList();
            if (member.HasThis && member.Name != ".ctor") expected.Insert(0, member.DeclaringType.FullName);
            if (!replacement.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(expected))
                throw new InvalidOperationException("Bridge argument mismatch: " + member.FullName);
            var expectedReturn = member.Name == ".ctor" ? member.DeclaringType.FullName : member.ReturnType.FullName;
            if (replacement.ReturnType.FullName != expectedReturn)
                throw new InvalidOperationException("Bridge return mismatch: " + member.FullName);
            edits.Add(new ApiCallSite(method.MetadataToken.ToInt32(), member.MetadataToken.ToInt32(), name, method.FullName));
            instruction.OpCode = OpCodes.Call;
            instruction.Operand = original.ImportReference(replacement);
        }
        if (unresolved.Count > 0) throw new InvalidOperationException("Unsupported missing game APIs:\n" + string.Join("\n", unresolved));
        if (edits.Count != 18) throw new InvalidOperationException($"Expected 18 reviewed call sites, found {edits.Count}.");
        var brokenNames = edits.Select(e => e.MethodName).ToHashSet();
        var brokenCallers = AllTypes(original.Types).SelectMany(t => t.Methods).Where(m => m.HasBody &&
            m.Body.Instructions.Any(i => i.Operand is MethodReference called && brokenNames.Contains(called.FullName)))
            .Select(m => originalMethodTokens[m.FullName]).ToArray();

        var rngEdits = new List<int>();
        var rewardRng = original.ImportReference(api.Methods.Single(m => m.Name == "RewardRng"));
        foreach (var type in AllTypes(original.Types))
        {
            var root = type;
            while (root.DeclaringType != null) root = root.DeclaringType;
            if (!root.Name.StartsWith("Improved", StringComparison.Ordinal)) continue;
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var instructions = method.Body.Instructions;
                for (var i = 0; i + 2 < instructions.Count; i++)
                {
                    if (instructions[i].Operand is not MethodReference { Name: "get_RunState", DeclaringType.Name: "Player" } ||
                        instructions[i+1].Operand is not MethodReference { Name: "get_Rng" } ||
                        instructions[i+2].Operand is not MethodReference rng ||
                        rng.DeclaringType.Name != "RunRngSet" || rng.Name is not ("get_Niche" or "get_CombatPotionGeneration")) continue;
                    instructions[i].OpCode = OpCodes.Call;
                    instructions[i].Operand = rewardRng;
                    instructions[i+1].OpCode = instructions[i+2].OpCode = OpCodes.Nop;
                    instructions[i+1].Operand = instructions[i+2].Operand = null;
                    rngEdits.Add(method.MetadataToken.ToInt32());
                }
            }
        }
        if (rngEdits.Count != 14) throw new InvalidOperationException($"Expected 14 reward RNG sites, found {rngEdits.Count}.");

        using var output = new MemoryStream();
        original.Write(output);
        return new RewriteResult(output.ToArray(), edits.Count, rngEdits.Count, 3,
            new RuntimePatchPlan(edits.ToArray(), rngEdits.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count()), originalMethodTokens, brokenCallers));
    }

    static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var t in types)
        {
            yield return t;
            foreach (var nested in AllTypes(t.NestedTypes)) yield return nested;
        }
    }
}
