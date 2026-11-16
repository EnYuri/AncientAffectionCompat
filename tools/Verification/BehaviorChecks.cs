using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using AncientAffectionCompat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Entities.Merchant;

internal static class BehaviorChecks
{
    private static Type[] _modTypes = [];
    private static bool ModTypes(ref Type[] __result) { __result = _modTypes; return false; }
    private static IEnumerable<CodeInstruction> ReturnFalse(IEnumerable<CodeInstruction> instructions) => [new(System.Reflection.Emit.OpCodes.Ldc_I4_0), new(System.Reflection.Emit.OpCodes.Ret)];
    private static bool Print(string __1) { Console.WriteLine(__1); return false; }
    private static bool BlockSaves() => throw new InvalidOperationException("Verification must not access SaveManager.");
    private static List<Reward> _offered = [];
    private static List<ModelId[]> _choices = [];
    private static decimal _strength;
    private static CardModel? _exhausted;
    private static bool CaptureRewards(List<Reward> __1, ref Task __result)
    { _offered = __1; __result = Task.CompletedTask; return false; }
    private static bool SelectRelic(Player __0, IReadOnlyList<RelicModel> __1, ref Task<RelicModel> __result)
    {
        Check(__1.Count == 3 && __1.All(r=>r.IsWax), "Toy Box: three wax choices");
        Check(__1.All(r=>__0.Relics.All(owned=>owned.Id!=r.Id)), "Toy Box: exclude acquired relics");
        _choices.Add(__1.Select(r=>r.Id).ToArray());
        __result = Task.FromResult(__1[1]); return false;
    }
    private static bool ObtainRelic(RelicModel __0, Player __1, ref Task<RelicModel> __result)
    { __1.AddRelicInternal(__0, silent: true); __result=Task.FromResult(__0); return false; }
    private static bool Strength(decimal __2, ref Task<StrengthPower?> __result)
    { _strength+=__2; __result=Task.FromResult<StrengthPower?>(null); return false; }
    public static Task<StrengthPower?> RecordStrength(PlayerChoiceContext context, Creature target, decimal amount, Creature? applier, CardModel? card, bool silent)
    { _strength+=amount; return Task.FromResult<StrengthPower?>(null); }
    private static IEnumerable<CodeInstruction> CaptureMittens(IEnumerable<CodeInstruction> instructions)
    {
        foreach(var i in instructions)
        {
            if(i.operand is MethodInfo m && m.DeclaringType==typeof(PowerCmd) && m.Name=="Apply" && m.IsGenericMethod)
                i.operand=AccessTools.Method(typeof(BehaviorChecks),nameof(RecordStrength));
            yield return i;
        }
    }
    private static bool Exhaust(CardModel __1, ref Task<CardPileAddResult?> __result)
    { _exhausted=__1; __result=Task.FromResult<CardPileAddResult?>(null); return false; }
    private static bool Shuffle(ref Task __result) { __result=Task.CompletedTask; return false; }
    private static void Check(bool condition, string text) { if(!condition) throw new Exception(text); }
    private static RelicModel Relic(string name, Player owner)
    { var r=ModelDb.GetById<RelicModel>(ModelDb.GetId(CompatMod.Type(name))).ToMutable(); r.Owner=owner; return r; }
    private static Player CreatePlayer(ulong id) => (Player)typeof(Player).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke(
        [ModelDb.Character<Ironclad>(), id, 80, 80, 3, 99, 3, 0, new RelicGrabBag(), UnlockState.all, null, null, null, null, null]);
    internal static async Task Run(Assembly ancient)
    {
        _modTypes = ancient.GetTypes();
        new Harmony("compat.verification").Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.Logger), "GetIsRunningFromGodotEditor"),
            transpiler: new HarmonyMethod(typeof(BehaviorChecks), nameof(ReturnFalse)));
        new Harmony("compat.verification").Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.ConsoleLogPrinter), "Print"),
            prefix: new HarmonyMethod(typeof(BehaviorChecks), nameof(Print)));
        new Harmony("compat.verification").Patch(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Saves.SaveManager), "Instance"),
            prefix: new HarmonyMethod(typeof(BehaviorChecks), nameof(BlockSaves)));
        new Harmony("compat.verification").Patch(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Helpers.ReflectionHelper), "ModTypes"),
            prefix: new HarmonyMethod(typeof(BehaviorChecks), nameof(ModTypes)));
        // Isolate the original mod's fallback data as well as the game's save singleton.
        var store = CompatMod.Type("AncientAffectionStore");
        AccessTools.Field(store,"_isRegistered").SetValue(null,true);
        AccessTools.Field(store,"_storeUnavailable").SetValue(null,true);
        AccessTools.Field(store,"_fallbackLoaded").SetValue(null,true);
        AccessTools.Field(store,"_fallbackData").SetValue(null,Activator.CreateInstance(CompatMod.Type("AncientAffectionStore+Data")));
        MegaCrit.Sts2.Core.Modding.AssemblyInfo.Init();
        MegaCrit.Sts2.Core.Modding.AssemblyInfo.MockTypes = _modTypes.ToDictionary(t=>t, _=>((MegaCrit.Sts2.Core.Modding.Mod?)null, true));
        ModelDb.Init();
        var relicTypes = ancient.GetTypes().Where(t => !t.IsAbstract && typeof(RelicModel).IsAssignableFrom(t)).ToArray();
        foreach(var type in relicTypes) ModelDb.Inject(type);
        MegaCrit.Sts2.Core.Multiplayer.Serialization.ModelIdSerializationCache.Init();
        ModelDb.InitIds();
        Console.WriteLine($"Instantiated {relicTypes.Length} added relic models.");
        var owner = CreatePlayer(1);
        var run = RunState.CreateForTest([owner], seed: "COMPATTEST");
        foreach(var type in relicTypes)
        {
            var relic = ModelDb.GetById<RelicModel>(ModelDb.GetId(type)).ToMutable();
            relic.Owner = owner;
            _ = relic.DynamicVars;
        }
        Console.WriteLine("PASS: all added relics clone and initialize dynamic variables.");
        var removal=new MerchantCardRemovalEntry(owner);
        var inventory=new MerchantInventory(owner,[],[],[],[],removal,[]);
        CompatMod.Call("MerchantFavorPatch","RememberMerchantInventory",inventory);
        Check(ApiBridge.AllEntries(inventory).SequenceEqual(inventory.AllEntries),"Merchant inventory API adapter");
        var entryKey=CompatMod.Call("MerchantFavorPatch","BuildMerchantBargainEntryKey",inventory,removal);
        Check(entryKey is string {Length:>0},"Merchant entry key");
        Console.WriteLine("PASS: original merchant inventory bookkeeping and bargain key execute against the new AllEntries API.");
        var h=new Harmony("compat.verification.behavior");
        h.Patch(AccessTools.Method(typeof(RewardsCmd),"OfferCustom"),prefix:new HarmonyMethod(typeof(BehaviorChecks),nameof(CaptureRewards)));
        foreach(var tier in new[]{"Devoted","Beloved"})
        {
            await Relic(tier+"GlassEye",owner).AfterObtained();
            Check(_offered.Count==5,"Glass Eye: five rewards");
            var rarities = _offered.Cast<CardReward>().Select(reward=>
            {
                Check((int)AccessTools.Property(typeof(CardReward),"OptionCount").GetValue(reward)! == 3,"Glass Eye: three choices");
                var options=(CardCreationOptions)AccessTools.Property(typeof(CardReward),"Options").GetValue(reward)!;
                return owner.Character.CardPool.AllCards.Where(options.CardPoolFilter!).Select(c=>c.Rarity).Distinct().Single();
            }).ToArray();
            Check(rarities.SequenceEqual(new[]{CardRarity.Common,CardRarity.Common,CardRarity.Uncommon,CardRarity.Uncommon,CardRarity.Rare}),"Glass Eye rarity sequence");
        }
        Console.WriteLine("PASS: both Glass Eyes, five rewards, three choices each, Common/Common/Uncommon/Uncommon/Rare.");
        foreach(var tier in new[]{"Devoted","Beloved"})
        {
            await Relic(tier+"Kaleidoscope",owner).AfterObtained();
            Check(_offered.Count==2,"Kaleidoscope: two rewards");
            foreach(var reward in _offered.Cast<CardReward>())
            {
                var cards=reward.Cards.ToList();
                Check(cards.Count==(tier=="Devoted"?4:5),"Kaleidoscope: description option count");
                Check(cards.Select(c=>c.Id).Distinct().Count()==cards.Count,"Kaleidoscope: distinct cards");
                var foreign=owner.UnlockState.CharacterCardPools.Where(p=>p!=owner.Character.CardPool).SelectMany(p=>p.AllCards).Select(c=>c.Id).ToHashSet();
                Check(cards.All(c=>foreign.Contains(c.Id)),"Kaleidoscope: foreign card pools");
                var reroll=(CardCreationOptions)AccessTools.Property(typeof(CardReward),"RerollOptions").GetValue(reward)!;
                Check(!reroll.CardPools.Contains(owner.Character.CardPool),"Kaleidoscope: foreign rerolls");
            }
        }
        Console.WriteLine("PASS: both Kaleidoscopes, two rewards of four/five distinct foreign cards; foreign reroll pools.");
        h.Patch(AccessTools.Method(typeof(RelicSelectCmd),"FromChooseARelicScreen"),prefix:new HarmonyMethod(typeof(BehaviorChecks),nameof(SelectRelic)));
        h.Patch(typeof(RelicCmd).GetMethods().Single(m=>m.Name=="Obtain"&&!m.IsGenericMethod),prefix:new HarmonyMethod(typeof(BehaviorChecks),nameof(ObtainRelic)));
        foreach(var tier in new[]{"Devoted","Beloved"})
        {
            _choices.Clear(); var before=owner.Relics.Count;
            await Relic(tier+"ToyBox",owner).AfterObtained();
            Check(_choices.Count==4&&owner.Relics.Count==before+4,"Toy Box: four selections/four obtains");
        }
        Console.WriteLine("PASS: both Toy Boxes, four rounds of three wax choices, acquired relic exclusion.");
        var mittensState=CompatMod.Method("ImprovedToastyMittens","BeforeHandDraw").GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()!.StateMachineType;
        h.Patch(AccessTools.Method(mittensState,"MoveNext"),transpiler:new HarmonyMethod(typeof(BehaviorChecks),nameof(CaptureMittens)));
        h.Patch(AccessTools.Method(typeof(CardCmd),"Exhaust"),prefix:new HarmonyMethod(typeof(BehaviorChecks),nameof(Exhaust)));
        h.Patch(AccessTools.Method(typeof(CardPileCmd),"ShuffleIfNecessary"),prefix:new HarmonyMethod(typeof(BehaviorChecks),nameof(Shuffle)));
        var other=CreatePlayer(2);
        foreach(var tier in new[]{"Devoted","Beloved"})
        {
            var relic=Relic(tier+"ToastyMittens",owner);
            var combat=new PlayerCombatState(owner);
            AccessTools.Property(typeof(Player),"PlayerCombatState").SetValue(owner,combat);
            var top=run.CreateCard<StrikeIronclad>(owner);
            // Populate the pile directly; actual Exhaust is intercepted to assert the target.
            combat.DrawPile.AddInternal(top, silent: true);
            var context=new ThrowingPlayerChoiceContext();
            for(var turn=1;turn<=4;turn++)
            {
                _strength=0; _exhausted=null;
                await relic.BeforeHandDraw(other,context,null!);
                Check(_strength==0&&_exhausted==null,"Mittens: ignore another player");
                await relic.BeforeHandDraw(owner,context,null!);
                Check(_strength==1,$"Mittens: one Strength per owner turn (tier={tier}, turn={turn}, actual={_strength}, dynamic={relic.DynamicVars.Strength.BaseValue}, ownerMatch={owner.Creature.Player==owner})");
                Check(_exhausted==(turn>(tier=="Devoted"?1:2)?top:null),"Mittens: safe turns / top draw card exhaustion");
                combat.IncrementTurnNumber();
            }
        }
        Console.WriteLine("PASS: both Mittens, turns one through four, owner gating, Strength and draw-pile exhaust threshold.");
        var multi=RunState.CreateForTest([CreatePlayer(3),CreatePlayer(4)],seed:"COMPATTEST");
        foreach(var name in new[]{"SilverCrucible","WingedBoots","MassiveScroll"})
        foreach(var tier in new[]{"Devoted","Beloved"})
        {
            var relic=Relic(tier+name,owner);
            Check(relic.IsAllowed(run)==(name!="MassiveScroll"),name+" single-player eligibility");
            Check(relic.IsAllowed(multi)==(name=="MassiveScroll"),name+" multiplayer eligibility");
        }
        Console.WriteLine("PASS: all six migrated single/multiplayer eligibility hooks.");
        await MultiplayerChecks.Run(run);
        var first=CreatePlayer(10); var second=CreatePlayer(11);
        var orderRun=RunState.CreateForTest([first,second],seed:"REWARDORDER");
        var sharedBefore=System.Text.Json.JsonSerializer.Serialize(orderRun.Rng.ToSerializable());
        var firstDraw=ApiBridge.RewardRng(first).NextInt(100000);
        var secondDraw=ApiBridge.RewardRng(second).NextInt(100000);
        Check(System.Text.Json.JsonSerializer.Serialize(orderRun.Rng.ToSerializable())==sharedBefore,"Rewards must not advance shared/combat RNG");
        first.InitializeSeed("REWARDORDER"); second.InitializeSeed("REWARDORDER");
        Check(ApiBridge.RewardRng(second).NextInt(100000)==secondDraw&&ApiBridge.RewardRng(first).NextInt(100000)==firstDraw,"Other player's reward order must not change results");
        Console.WriteLine("PASS: per-player reward RNG isolates acquisition order and leaves shared/combat RNG unchanged.");
    }
}
