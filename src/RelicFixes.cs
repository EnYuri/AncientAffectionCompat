using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace AncientAffectionCompat;

internal static class RelicFixes
{
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(CompatMod.Method("ImprovedToyBox", "AfterObtained"),
            prefix: new HarmonyMethod(typeof(RelicFixes), nameof(ToyBoxPrefix)));
        harmony.Patch(CompatMod.Method("ImprovedKaleidoscope", "AfterObtained"),
            prefix: new HarmonyMethod(typeof(RelicFixes), nameof(KaleidoscopePrefix)));
    }

    private static bool ToyBoxPrefix(RelicModel __instance, ref Task __result)
    { __result = ToyBox(__instance); return false; }

    private static bool KaleidoscopePrefix(RelicModel __instance, ref Task __result)
    { __result = Kaleidoscope(__instance); return false; }

    internal static async Task Kaleidoscope(RelicModel relic)
    {
        var owner = relic.Owner;
        var pools = owner.UnlockState.CharacterCardPools.Where(p => p != owner.Character.CardPool)
            .OrderBy(p => p.Id).ToList();
        if (pools.Count == 0) throw new InvalidOperationException("Kaleidoscope requires another character's card pool.");
        var rewards = new List<Reward>();
        var reroll = CardCreationOptions.ForNonCombatWithDefaultOdds(pools)
            .WithFlags(CardCreationFlags.NoCardPoolModifications);
        for (var round = 0; round < relic.DynamicVars.Cards.IntValue; round++)
        {
            var shuffled = pools.StableShuffle(owner.PlayerRng.NonCombatGeneration).ToList();
            var cards = new List<CardModel>();
            // Cycle through other characters again when the promised number exceeds
            // the number of pools. Each choice is still a distinct foreign card.
            for (var i = 0; i < relic.DynamicVars["Options"].IntValue; i++)
            {
                var options = new CardCreationOptions([shuffled[i % shuffled.Count]], CardCreationSource.Other,
                    CardRarityOddsType.RegularEncounter, c => cards.All(chosen => chosen.Id != c.Id))
                    .WithFlags(CardCreationFlags.NoCardPoolModifications);
                cards.Add(CardFactory.CreateForReward(owner, 1, options).Single().Card);
            }
            rewards.Add(new CardReward(cards, CardCreationSource.Other, owner, reroll));
        }
        await RewardsCmd.OfferCustom(owner, rewards);
    }

    internal static async Task ToyBox(RelicModel relic)
    {
        for (var round = 0; round < relic.DynamicVars["Relics"].IntValue; round++)
        {
            // The original picker sorts by stable model IDs and filters already-owned relics.
            var options = (List<RelicModel>)CompatMod.Call("AncientAffectionRelicRewardPicker", "Select",
                relic.Owner, $"{relic.GetType().Name}:compat-choice:{round}", 3, null, null)!;
            foreach (var option in options) option.IsWax = true;
            var selected = await RelicSelectCmd.FromChooseARelicScreen(relic.Owner, options);
            if (selected != null) await RelicCmd.Obtain(selected, relic.Owner);
        }
    }
}
