using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 4)
    throw new ArgumentException("Usage: PatchBuilder <game-directory> <original-mod-directory> <compat-dll> <output-directory>");
var gameDir = Path.GetFullPath(args[0]);
var gameInfo = JsonNode.Parse(File.ReadAllText(Path.Combine(gameDir, "release_info.json")))!;
if (gameInfo["version"]?.GetValue<string>() != "v0.113.1")
    throw new InvalidOperationException("This patch targets game v0.113.1. Review and rebuild for other versions.");
var sourceDir = Path.GetFullPath(args[1]);
var bridgePath = Path.GetFullPath(args[2]);
var outputDir = Path.GetFullPath(args[3]);
if (outputDir.Equals(sourceDir, StringComparison.OrdinalIgnoreCase) ||
    outputDir.StartsWith(sourceDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Output must be separate from the original mod.");
var input = Path.Combine(sourceDir, "AncientAffection.dll");
var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
const string supportedHash = "20FB2DCEEA8D83DB5596044EB09F9C3AA9B728B32A71C9FB112E4232561FC085";
if (hash != supportedHash)
    throw new InvalidOperationException("This original DLL has not been reviewed. Refusing to apply version-specific changes: " + hash);

using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.Combine(gameDir, "data_sts2_windows_x86_64"));
resolver.AddSearchDirectory(Path.GetDirectoryName(bridgePath)!);
using var original = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver });
using var bridge = ModuleDefinition.ReadModule(bridgePath, new ReaderParameters { AssemblyResolver = resolver });
var api = bridge.GetType("AncientAffectionCompat.ApiBridge");
var edits = new List<object>();
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
    edits.Add(new { method = method.FullName, oldReference = member.FullName, bridge = name });
    instruction.OpCode = OpCodes.Call;
    instruction.Operand = original.ImportReference(replacement);
}
if (unresolved.Count > 0) throw new InvalidOperationException("Unsupported missing game APIs:\n" + string.Join("\n", unresolved));
if (edits.Count != 18) throw new InvalidOperationException($"Expected 18 reviewed call sites, found {edits.Count}.");

var rngEdits = new List<string>();
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
            rngEdits.Add(method.FullName);
        }
    }
}
if (rngEdits.Count != 14) throw new InvalidOperationException($"Expected 14 reward RNG sites, found {rngEdits.Count}.");

// IsAllowed became a non-virtual guard in 0.113.1. Move the original mode restrictions
// to the replacement virtual hook so they are still dispatched through RelicModel.
foreach (var name in new[] { "ImprovedSilverCrucible", "ImprovedWingedBoots", "ImprovedMassiveScroll" })
{
    var method = original.GetType("AncientAffection." + name).Methods.Single(m => m.Name == "IsAllowed");
    method.Name = "IsAllowedInternal";
    method.IsPublic = false;
    method.IsFamily = true;
}

// Initialize runtime fixes only after the original has registered all of its content/patches.
var initialize = original.GetType("AncientAffection.MainFile").Methods.Single(m => m.Name == "Initialize");
var hook = original.ImportReference(bridge.GetType("AncientAffectionCompat.CompatMod").Methods.Single(m => m.Name == "InitializeAfterAncient"));
foreach (var ret in initialize.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
{
    ret.OpCode = OpCodes.Call;
    ret.Operand = hook;
    initialize.Body.GetILProcessor().InsertAfter(ret, Instruction.Create(OpCodes.Ret));
}
var originalOut = Path.Combine(outputDir, "AncientAffection");
var compatOut = Path.Combine(outputDir, "AncientAffectionCompat");
Directory.CreateDirectory(originalOut);
Directory.CreateDirectory(compatOut);
original.Write(Path.Combine(originalOut, "AncientAffection.dll"));
File.Copy(Path.Combine(sourceDir, "AncientAffection.pck"), Path.Combine(originalOut, "AncientAffection.pck"), true);
var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(sourceDir, "AncientAffection.json")))!;
manifest["name"] = "Ancient Affection (Compatibility Patch)";
manifest["version"] = "v0.1.0+compat.1";
manifest["min_game_version"] = "0.113.1";
manifest["dependencies"]!.AsArray().Add(new JsonObject { ["id"] = "AncientAffectionCompat", ["min_version"] = "0.1.0" });
File.WriteAllText(Path.Combine(originalOut, "AncientAffection.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
File.Copy(bridgePath, Path.Combine(compatOut, "AncientAffectionCompat.dll"), true);
File.Copy(Path.Combine(Path.GetDirectoryName(bridgePath)!, "AncientAffectionCompat.json"), Path.Combine(compatOut, "AncientAffectionCompat.json"), true);
File.WriteAllText(Path.Combine(outputDir, "patch-report.json"), JsonSerializer.Serialize(new
{
    originalSha256 = hash,
    patchedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(originalOut, "AncientAffection.dll")))),
    pckSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(originalOut, "AncientAffection.pck")))) ,
    game = gameInfo,
    edits,
    rngEdits,
    migratedOverrides = new[] { "ImprovedSilverCrucible", "ImprovedWingedBoots", "ImprovedMassiveScroll" }
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Patched {edits.Count} API call sites, {rngEdits.Count} reward RNG sites, 3 eligibility hooks. Original DLL and PCK unchanged. Output: {outputDir}");

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var t in types)
    {
        yield return t;
        foreach (var nested in AllTypes(t.NestedTypes)) yield return nested;
    }
}
