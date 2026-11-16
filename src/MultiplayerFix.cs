using System.Collections;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace AncientAffectionCompat;

internal static class MultiplayerFix
{
    private const string ChoicePatch = "AncientAffectionRelicChoicePatch";
    private const string PlanType = "AncientAffectionMultiplayerOptionPlan";
    private const string StoreType = "AncientAffectionStore";
    private static readonly ConditionalWeakTable<AncientEventModel, Profile> Profiles = new();
    [ThreadStatic] private static Profile? _scope;
    private static readonly string[] Keys = ["NEOW", "OROBAS", "NONUPEIPE", "PAEL", "DARV", "TEZCATARA", "VAKUU", "TANX"];

    internal sealed record Profile(string Key, int Affection, bool Married, bool Upgrades, bool Hell,
        int AscensionChance, bool GuaranteeAscension, bool HostOwner)
    {
        internal List<int> Encode() => [0x41414331, 1, Array.IndexOf(Keys, Key), Affection,
            Married ? 1 : 0, Upgrades ? 1 : 0, Hell ? 1 : 0, AscensionChance,
            GuaranteeAscension ? 1 : 0, HostOwner ? 1 : 0];
        internal static Profile Decode(IReadOnlyList<int> data, string key)
        {
            if (data.Count != 10 || data[0] != 0x41414331 || data[1] != 1 ||
                Array.IndexOf(Keys, key) < 0 || data[2] != Array.IndexOf(Keys, key) || data[7] is < 0 or > 100 ||
                new[] { 4, 5, 6, 8, 9 }.Any(i => data[i] is < 0 or > 1))
                throw new InvalidDataException("Ancient Affection profile synchronization mismatch.");
            return new(key, data[3], data[4] != 0, data[5] != 0, data[6] != 0, data[7], data[8] != 0, data[9] != 0);
        }
    }

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(AncientEventModel), "BeforeEventStarted"),
            postfix: new HarmonyMethod(typeof(MultiplayerFix), nameof(BeforeEventPostfix)));
        harmony.Patch(CompatMod.Method(ChoicePatch, "UpgradeMultiplayerRelicOptions"),
            prefix: new HarmonyMethod(typeof(MultiplayerFix), nameof(UpgradeOptions)));
        harmony.Patch(CompatMod.Method(StoreType, "Get"), prefix: new HarmonyMethod(typeof(MultiplayerFix), nameof(GetAffection)));
        harmony.Patch(CompatMod.Method(StoreType, "IsMarried"), prefix: new HarmonyMethod(typeof(MultiplayerFix), nameof(GetMarried)));
        foreach (var getter in new[] { "get_AffectionRelicUpgradesEnabled", "get_HellModeEnabled" })
            harmony.Patch(CompatMod.Method(StoreType, getter), prefix: new HarmonyMethod(typeof(MultiplayerFix), nameof(GetFlag)));
        harmony.Patch(CompatMod.Method(StoreType, "get_NeowAscensionChancePercent"),
            prefix: new HarmonyMethod(typeof(MultiplayerFix), nameof(GetChance)));
        harmony.Patch(CompatMod.Method("AncientAffectionOptionFactory", "ShouldGuaranteeNeowAscension"),
            prefix: new HarmonyMethod(typeof(MultiplayerFix), nameof(GetGuarantee)));
    }

    private static void BeforeEventPostfix(AncientEventModel __instance, ref Task __result) =>
        __result = Synchronize(__instance, __result);

    private static async Task Synchronize(AncientEventModel ancient, Task original)
    {
        await original;
        object?[] keyArgs = [ancient, ""];
        if (!(bool)CompatMod.Method(ChoicePatch, "TryGetAncientKey").Invoke(null, keyArgs)!) return;
        var key = (string)keyArgs[1]!;
        if (!(bool)CompatMod.Call("AncientAffectionMultiplayer", "IsCurrentRunRealMultiplayer")!) return;
        var player = ancient.Owner ?? throw new InvalidOperationException("Ancient event has no owner.");
        var net = RunManager.Instance.NetService;
        var synchronizer = RunManager.Instance.PlayerChoiceSynchronizer;
        var profile = await SynchronizeProfile(player, key, net.Type, LocalContext.IsMe(player), synchronizer, () => Capture(key));
        Profiles.Remove(ancient);
        Profiles.Add(ancient, profile);
        CompatMod.Log($"Synchronized {key} profile for player {player.NetId} before generating relic choices.");
    }

    internal static async Task<Profile> SynchronizeProfile(Player player, string key, NetGameType mode,
        bool isLocal, PlayerChoiceSynchronizer synchronizer, Func<Profile> capture)
    {
        if (mode == NetGameType.Singleplayer) return capture();
        var choice = synchronizer.ReserveChoiceId(player);
        if (isLocal && mode != NetGameType.Replay)
        {
            var profile = capture();
            synchronizer.SyncLocalChoice(player, choice, PlayerChoiceResult.FromIndexes(profile.Encode()));
            return profile;
        }
        var result = await synchronizer.WaitForRemoteChoice(player, choice).WaitAsync(TimeSpan.FromSeconds(60));
        return Profile.Decode(result.AsIndexes(), key);
    }

    private static Profile Capture(string key) => new(key,
        (int)CompatMod.Call(StoreType, "Get", key)!, (bool)CompatMod.Call(StoreType, "IsMarried", key)!,
        (bool)CompatMod.Call(StoreType, "get_AffectionRelicUpgradesEnabled")!,
        (bool)CompatMod.Call(StoreType, "get_HellModeEnabled")!,
        (int)CompatMod.Call(StoreType, "get_NeowAscensionChancePercent")!,
        (bool)CompatMod.Call("AncientAffectionOptionFactory", "ShouldGuaranteeNeowAscension", key)!,
        (bool)CompatMod.Call("AncientAffectionMultiplayer", "IsHost")!);

    private static bool UpgradeOptions(AncientEventModel ancient, string ancientKey,
        IReadOnlyList<EventOption> originalOptions, ref IReadOnlyList<EventOption> __result)
    {
        if (!Profiles.TryGetValue(ancient, out var profile))
            throw new InvalidOperationException("No synchronized affection profile. All players must install the same compatibility patch.");
        var previous = _scope;
        var owner = ancient.Owner ?? throw new InvalidOperationException("Ancient event has no owner.");
        _scope = profile;
        try
        {
            var options = originalOptions.ToList();
            // Preserve the original host-only ascension policy, but make every peer agree on it.
            var ascensionIndex = profile.HostOwner
                ? (int)CompatMod.Call(ChoicePatch, "ReplaceMarriedFourthOptionWithAscension", ancient, ancientKey, options)!
                : -1;
            if (profile.Upgrades)
            {
                var indices = Enumerable.Range(0, options.Count).Where(i =>
                    options[i].Relic != null && !options[i].IsLocked && !options[i].IsProceed && i != ascensionIndex &&
                    !(bool)CompatMod.Call("AncientAffectionOptionFactory", "IsAffectionOption", options[i])! &&
                    !(bool)CompatMod.Call("AncientAffectionOptionFactory", "IsRedOption", options[i])!).ToList();
                var signature = (string)CompatMod.Call(ChoicePatch, "BuildMultiplayerRelicChoiceSignature", ancientKey, options, indices)!;
                var plan = BuildPlan(owner.NetId, profile, signature, options, indices);
                CompatMod.Call(ChoicePatch, "ApplyMultiplayerOptionPlan", ancient, ancientKey, options, plan);
                foreach (var i in indices)
                    options[i].BeforeChosen += _ => LocalContext.IsMe(ancient.Owner)
                        ? (Task)CompatMod.Call(ChoicePatch, "GrantRelicChoiceAffection", ancientKey)! : Task.CompletedTask;
            }
            __result = options;
            return false;
        }
        finally { _scope = previous; }
    }

    internal static object BuildPlan(ulong ownerId, Profile profile, string signature, IReadOnlyList<EventOption> options, IReadOnlyList<int> indices)
    {
        var ancientKey = profile.Key;
        var entryType = CompatMod.Type(PlanType + "+EventRelicOption");
        var entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
        foreach (var i in indices)
        {
            var entry = Activator.CreateInstance(entryType)!;
            entryType.GetProperty("Index")!.SetValue(entry, i);
            entryType.GetProperty("RelicCategory")!.SetValue(entry, options[i].Relic!.Id.Category);
            entryType.GetProperty("RelicEntry")!.SetValue(entry, options[i].Relic!.Id.Entry);
            entries.Add(entry);
        }
        // Populate the original plan builder's snapshot immediately from the synchronized
        // owner, never from the local machine's profile or a speculative remote request.
        var snapshotType = CompatMod.Type(PlanType + "+AffectionSnapshot");
        var snapshot = Activator.CreateInstance(snapshotType)!;
        snapshotType.GetProperty("PlayerNetId")!.SetValue(snapshot, ownerId);
        snapshotType.GetProperty("AffectionByAncient")!.SetValue(snapshot, new Dictionary<string, int> { [ancientKey] = profile.Affection });
        snapshotType.GetProperty("MarriedByAncient")!.SetValue(snapshot, new Dictionary<string, bool> { [ancientKey] = profile.Married });
        CompatMod.Call(PlanType, "StoreSnapshot", snapshot);
        var request = CompatMod.Call(PlanType, "CreateRequest", ownerId, ancientKey, signature, entries, profile.Affection, profile.Married);
        return CompatMod.Call(PlanType, "BuildPlan", request)!;
    }

    private static bool GetAffection(string __0, ref int __result)
    { if (_scope?.Key != __0) return true; __result = _scope.Affection; return false; }
    private static bool GetMarried(string __0, ref bool __result)
    { if (_scope?.Key != __0) return true; __result = _scope.Married; return false; }
    private static bool GetFlag(System.Reflection.MethodBase __originalMethod, ref bool __result)
    { if (_scope == null) return true; __result = __originalMethod.Name == "get_HellModeEnabled" ? _scope.Hell : _scope.Upgrades; return false; }
    private static bool GetChance(ref int __result)
    { if (_scope == null) return true; __result = _scope.AscensionChance; return false; }
    private static bool GetGuarantee(string __0, ref bool __result)
    { if (_scope?.Key != __0) return true; __result = _scope.GuaranteeAscension; return false; }
}
