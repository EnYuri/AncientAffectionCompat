using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace AncientAffectionCompat;

// Preserve the old evaluation-stack signatures while compiling each forwarded call against
// the installed game. These are called by the in-memory rewriter's explicit replacement list.
public static class ApiBridge
{
    public static string OriginalAssemblyLocation(System.Reflection.Assembly assembly) =>
        SubscriptionLoader.OriginalAssemblyPath ?? assembly.Location;
    // Pickup rewards can run in a different order for different players. They must not
    // consume the combat/shared random stream, which would change the next battle.
    public static MegaCrit.Sts2.Core.Random.Rng RewardRng(Player player) => player.PlayerRng.NonCombatGeneration;
    public static IEnumerable<MerchantEntry> AllEntries(MerchantInventory inventory) => inventory.AllEntries;
    public static RelicReward RelicReward(RelicModel relic, Player player) => new(relic, player, null);
    public static CardReward GeneratedCardReward(CardCreationOptions options, int count, Player player,
        PlayerChoiceSynchronizer? synchronizer) => new(options, count, player, synchronizer, null);
    public static CardReward OfferedCardReward(IEnumerable<CardModel> cards, CardCreationSource source,
        Player player, CardCreationOptions options, PlayerChoiceSynchronizer? synchronizer) =>
        new(cards, source, player, options, synchronizer, null);
    public static Task LoseMaxHp(PlayerChoiceContext context, Creature creature, decimal amount, bool legacyFlag) =>
        CreatureCmd.LoseMaxHp(context, creature, amount, null);
    public static Task<IEnumerable<CardModel>> ChooseBundle(Player player, IReadOnlyList<IReadOnlyList<CardModel>> bundles) =>
        CardSelectCmd.FromChooseABundleScreen(player, bundles, false);
    public static Task Exhaust(PlayerChoiceContext context, CardModel card, bool ethereal, bool skipVisuals) =>
        CardCmd.Exhaust(context, card, ethereal, skipVisuals);
    public static Task<bool> Purchase(MerchantEntry entry, MerchantInventory inventory, bool free) =>
        entry.OnTryPurchaseUnsynchronized(inventory, free);
    public static async Task<bool> RemoveCard(MerchantCardRemovalEntry entry, MerchantInventory inventory,
        bool free, bool cancelable)
    {
        var field = AccessTools.Field(typeof(MerchantCardRemovalEntry), "_isCancelable")
            ?? throw new MissingFieldException(typeof(MerchantCardRemovalEntry).FullName, "_isCancelable");
        var previous = (bool)field.GetValue(entry)!;
        field.SetValue(entry, cancelable);
        try { return await entry.OnTryPurchaseUnsynchronized(inventory, free); }
        finally { field.SetValue(entry, previous); }
    }
}
