using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace AncientAffectionCompat;

[ModInitializer(nameof(Initialize))]
public static class CompatMod
{
    internal const string HarmonyId = "Nayuri.AncientAffectionCompat";
    internal static Assembly Ancient = null!;
    private static bool _initialized;
    public static void Initialize() => SubscriptionLoader.Initialize();
    public static void InitializeAfterAncient()
    {
        if (_initialized) return;
        Ancient = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "AncientAffection");
        var harmony = new Harmony(HarmonyId);
        MultiplayerFix.Install(harmony);
        RelicFixes.Install(harmony);
        _initialized = true;
        Log("API bridge, multiplayer and relic fixes initialized.");
    }
    internal static Type Type(string name) => Ancient.GetType("AncientAffection." + name, true)!;
    internal static MethodInfo Method(string type, string name) => AccessTools.Method(Type(type), name)
        ?? throw new MissingMethodException(type, name);
    internal static object? Call(string type, string name, params object?[] args) => Method(type, name).Invoke(null, args);
    internal static void Log(string text) => Console.WriteLine("[AncientAffectionCompat] " + text);
}
