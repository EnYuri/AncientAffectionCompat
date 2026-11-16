using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AncientAffectionCompat;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

internal static class MultiplayerChecks
{
    internal static async Task Run(RunState run)
    {
        var player=run.Players[0];
        var ownerNet=DispatchProxy.Create<INetGameService,MemoryNetwork>();
        var remoteNet=DispatchProxy.Create<INetGameService,MemoryNetwork>();
        var owner=(MemoryNetwork)(object)ownerNet;
        var remote=(MemoryNetwork)(object)remoteNet;
        owner.Id=player.NetId; remote.Id=999;
        owner.Peer=remote; remote.Peer=owner;
        using var sender=new PlayerChoiceSynchronizer(ownerNet,run);
        using var receiver=new PlayerChoiceSynchronizer(remoteNet,run);
        foreach(var early in new[]{false,true})
        {
            var source=new MultiplayerFix.Profile("NEOW",200,true,true,false,73,true,true);
            Task<MultiplayerFix.Profile> wait;
            if(early)
            {
                await MultiplayerFix.SynchronizeProfile(player,"NEOW",NetGameType.Host,true,sender,()=>source);
                wait=MultiplayerFix.SynchronizeProfile(player,"NEOW",NetGameType.Client,false,receiver,()=>throw new Exception("Remote used local profile"));
            }
            else
            {
                wait=MultiplayerFix.SynchronizeProfile(player,"NEOW",NetGameType.Client,false,receiver,()=>throw new Exception("Remote used local profile"));
                Check(!wait.IsCompleted,"Remote must wait for owner's profile");
                await MultiplayerFix.SynchronizeProfile(player,"NEOW",NetGameType.Host,true,sender,()=>source);
            }
            Check(await wait.WaitAsync(TimeSpan.FromSeconds(2))==source,"Owner profile round trip");
            Check(sender.ChoiceIds.SequenceEqual(receiver.ChoiceIds),"Choice counters remain synchronized");
        }
        var valid=new MultiplayerFix.Profile("NEOW",200,true,true,false,73,true,true);
        foreach(var mutate in new Action<List<int>>[]{d=>d.RemoveAt(0),d=>d[0]=0,d=>d[1]=99,d=>d[2]=4,d=>d[4]=2,d=>d[7]=101})
        {
            var data=valid.Encode(); mutate(data);
            try { MultiplayerFix.Profile.Decode(data,"NEOW"); throw new Exception("Malformed profile accepted"); }
            catch(InvalidDataException) { }
        }
        var options=new RelicModel[]{ModelDb.Relic<ToastyMittens>(),ModelDb.Relic<Kaleidoscope>(),ModelDb.Relic<GlassEye>()}
            .Select(r=>((EventOption)RuntimeHelpers.GetUninitializedObject(typeof(EventOption))).WithRelic(r.ToMutable())).ToList();
        static string Plan(ulong owner,MultiplayerFix.Profile p,List<EventOption> options) => JsonSerializer.Serialize(
            MultiplayerFix.BuildPlan(owner,p,"verification-fixed-options",options,[0,1,2]));
        var expected=Plan(player.NetId,valid,options);
        Check(!expected.Contains("\"Replacements\":[]"),"High affection actually produces an upgrade");
        // Poison the original snapshot cache with another profile, then rebuild with the owner.
        _=Plan(player.NetId,valid with{Affection=-100,Married=false},options);
        _=Plan(999,valid with{Affection=0,Married=false},options);
        Check(Plan(player.NetId,valid,options)==expected,"Foreign/stale snapshots cannot change the owner's upgrade plan");
        Check(Plan(player.NetId,MultiplayerFix.Profile.Decode(valid.Encode(),"NEOW"),options)==expected,"Peer receives identical relic IDs");
        Console.WriteLine("PASS: real PlayerChoiceSynchronizer early/late messages, owner-only profile capture, matching counters, payload validation, stable upgrade plans after stale/foreign snapshots.");
    }
    private static void Check(bool ok,string text) { if(!ok) throw new Exception(text); }
}

// Only the transport is in memory; the game's choice message and synchronizer are real.
public class MemoryNetwork : DispatchProxy
{
    public ulong Id;
    public MemoryNetwork Peer=null!;
    private readonly Dictionary<Type,Delegate> _handlers=[];
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        args??=[];
        switch(method!.Name)
        {
            case "get_NetId": return Id;
            case "get_Type": return NetGameType.Client;
            case "get_IsConnected": return true;
            case "RegisterMessageHandler": _handlers[method.GetGenericArguments()[0]]=(Delegate)args[0]!; return null;
            case "UnregisterMessageHandler": _handlers.Remove(method.GetGenericArguments()[0]); return null;
            case "SendMessage": Peer._handlers[method.GetGenericArguments()[0]].DynamicInvoke(args[0],Id); return null;
            default: throw new NotSupportedException(method.Name);
        }
    }
}
