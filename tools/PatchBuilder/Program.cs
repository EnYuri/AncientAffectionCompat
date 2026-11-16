using System.Text.Json;
using AncientAffectionCompat.Patching;

if (args.Length != 4)
    throw new ArgumentException("Usage: PatchBuilder <game-directory> <original-mod-directory> <compat-dll> <output-directory>");
var gameDir = Path.GetFullPath(args[0]);
var sourceDir = Path.GetFullPath(args[1]);
var bridgePath = Path.GetFullPath(args[2]);
var outputDir = Path.GetFullPath(args[3]);
if (outputDir.Equals(sourceDir, StringComparison.OrdinalIgnoreCase) ||
    outputDir.StartsWith(sourceDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Output must be separate from the original mod.");
using var release = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(gameDir, "release_info.json")));
if (release.RootElement.GetProperty("version").GetString() != AssemblyRewriter.SupportedGameVersion)
    throw new InvalidOperationException("This patch targets game v0.113.1.");
var originalBytes = File.ReadAllBytes(Path.Combine(sourceDir, "AncientAffection.dll"));
var result = AssemblyRewriter.Rewrite(originalBytes, Path.Combine(gameDir, "data_sts2_windows_x86_64"), bridgePath);
// The rewritten original is deliberately never written or distributed.
var modDir = Path.Combine(outputDir, "AncientAffectionCompat");
Directory.CreateDirectory(modDir);
var allowed = new[] { "AncientAffectionCompat.dll", "AncientAffectionCompat.json", "AncientAffectionCompat.Patching.dll", "Mono.Cecil.dll", "Mono.Cecil.LICENSE.txt" };
foreach (var existing in Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories))
    if (Path.GetDirectoryName(existing) != modDir || !allowed.Contains(Path.GetFileName(existing)))
        throw new InvalidOperationException("Unexpected file in workshop output; use a clean output directory: " + existing);
foreach (var file in allowed.Where(file => file.EndsWith(".dll") || file.EndsWith(".json")))
    File.Copy(Path.Combine(Path.GetDirectoryName(bridgePath)!, file), Path.Combine(modDir, file), true);
File.Copy(Path.Combine(AppContext.BaseDirectory, "Mono.Cecil.LICENSE.txt"), Path.Combine(modDir, "Mono.Cecil.LICENSE.txt"), true);
Console.WriteLine($"Workshop add-on prepared: {result.ApiCalls} API calls, {result.RewardRngSites} RNG sites, {result.EligibilityHooks} eligibility hooks. Original DLL/PCK not included. {outputDir}");
