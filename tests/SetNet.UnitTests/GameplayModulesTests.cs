using System;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Abilities;
using SetNet.BehaviorTree;
using SetNet.Combat;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.Dialogue;
using SetNet.Docs;
using SetNet.Equipment;
using SetNet.GeoData;
using SetNet.Inventory;
using SetNet.Mobs;
using SetNet.Notifications;
using SetNet.Persistence;
using SetNet.Spawning;
using SetNet.StateMachine;
using SetNet.Stats;
using SetNet.UnitTests.Integration;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for the gameplay foundation modules (Stats/Combat/Abilities/Equipment/AI/Spawning/Persistence/Docs).</summary>
public class GameplayModulesTests
{
    private static StatSchema CombatSchema() => StatSchema.Create()
        .Define("attack_power", 100, min: 0)
        .Define("defense", 0, min: 0)
        .Define("crit_chance", 0, min: 0, max: 1)
        .Define("crit_mult", 0.5)
        .Define("move_speed", 5, min: 0)
        .Build();

    // ---- Stats ----

    [Fact]
    public void Stats_ComputesModifiersInOrder_AndClamps()
    {
        var set = StatSchema.Create().Define("hp", 100, min: 0, max: 200).Build().NewSet();
        Assert.Equal(100, set.Get("hp"));

        set.AddModifier(StatModifier.Flat("hp", 50));               // 150
        set.AddModifier(StatModifier.PercentAdd("hp", 0.10));       // ×1.10 → 165
        Assert.Equal(165, set.Get("hp"), 3);

        set.AddModifier(StatModifier.PercentMult("hp", 1.0));       // ×2 → 330, clamped to 200
        Assert.Equal(200, set.Get("hp"), 3);
    }

    [Fact]
    public void Stats_RemoveBySource_RevertsAndInvalidatesCache()
    {
        var set = StatSchema.Create().Define("atk", 10).Build().NewSet();
        var gear = new object();
        set.AddModifiers(new[] { StatModifier.Flat("atk", 5, gear), StatModifier.PercentAdd("atk", 0.5, gear) });
        Assert.Equal((10 + 5) * 1.5, set.Get("atk"), 3);   // 22.5 (also warms the cache)
        set.RemoveBySource(gear);
        Assert.Equal(10, set.Get("atk"), 3);               // cache correctly invalidated
    }

    [Fact]
    public void Stats_SetBase_Overrides()
    {
        var set = StatSchema.Create().Define("level", 1).Build().NewSet();
        set.SetBase("level", 5);
        Assert.Equal(5, set.Get("level"));
        set.ResetBase("level");
        Assert.Equal(1, set.Get("level"));
    }

    // ---- Combat ----

    [Fact]
    public void Combat_MitigatesByDefense_AndCanCrit()
    {
        var schema = CombatSchema();
        var attacker = schema.NewSet();                 // attack_power 100, crit_chance 0
        var defender = schema.NewSet();
        defender.SetBase("defense", 100);               // armor constant 100 → 50% mitigation

        var resolver = new CombatResolver();
        var r = resolver.Resolve(attacker, defender, new AttackSpec(1.0));
        Assert.False(r.IsCrit);
        Assert.Equal(50, r.Amount, 2);                  // 100 raw − 50 mitigated

        attacker.SetBase("crit_chance", 1.0);           // always crits (crit_mult 0.5 → ×1.5)
        var c = resolver.Resolve(attacker, defender, new AttackSpec(1.0));
        Assert.True(c.IsCrit);
        Assert.Equal(75, c.Amount, 2);                  // 150 raw − 75 mitigated
    }

    [Fact]
    public void Combat_Health_AppliesAndDies()
    {
        var hp = new Health(200);
        Assert.False(hp.Apply(50));
        Assert.Equal(150, hp.Current, 3);
        Assert.True(hp.Apply(500));                     // overkill
        Assert.Equal(0, hp.Current, 3);
        Assert.False(hp.IsAlive);
    }

    // ---- Abilities (server-side logic; no network) ----

    [Fact]
    public async Task Abilities_Use_DealsDamage_SpendsResource_AndCoolsDown()
    {
        var schema = StatSchema.Create().Define("attack_power", 40, min: 0).Build();
        var casterStats = schema.NewSet();
        var targetStats = schema.NewSet();
        var targetHp = new Health(100);
        var mana = new ResourcePool(100);

        var server = new TestServer(Config(6101));
        var abilities = server.UseAbilities(new AbilityOptions
        {
            StatsOf = k => k == "caster" ? casterStats : k == "target" ? targetStats : null,
            HealthOf = k => k == "target" ? targetHp : null,
            PositionOf = k => k == "caster" ? new AbilityPoint(0, 0, 0) : k == "target" ? new AbilityPoint(2, 0, 0) : (AbilityPoint?)null,
            ResourceOf = (k, r) => k == "caster" && r == "mana" ? mana : null,
        });
        abilities.Define(new AbilityDefinition
        {
            Id = "strike", CooldownMs = 1000, Range = 5, ResourceId = "mana", ResourceCost = 30, TargetKind = TargetKind.Target,
            Effects = { new DamageEffect(coefficient: 1.0, damageType: "physical") },
        });

        var outcome = await abilities.TryUseAsync("caster", "strike", AbilityTarget.Of("target"));
        Assert.True(outcome.Ok);
        Assert.Equal(60, targetHp.Current, 2);          // 100 − 40 (no defense)
        Assert.Equal(70, mana.Current, 2);              // 100 − 30

        var again = await abilities.TryUseAsync("caster", "strike", AbilityTarget.Of("target"));
        Assert.False(again.Ok);                         // on cooldown
        Assert.Contains("cooldown", again.Message);
    }

    [Fact]
    public async Task Abilities_OutOfRange_Fails()
    {
        var server = new TestServer(Config(6102));
        var abilities = server.UseAbilities(new AbilityOptions
        {
            StatsOf = _ => StatSchema.Create().Define("attack_power", 10).Build().NewSet(),
            HealthOf = _ => new Health(100),
            PositionOf = k => k == "caster" ? new AbilityPoint(0, 0, 0) : new AbilityPoint(10, 0, 0),
        });
        abilities.Define(new AbilityDefinition { Id = "poke", Range = 1, TargetKind = TargetKind.Target, Effects = { new DamageEffect(1.0) } });

        var outcome = await abilities.TryUseAsync("caster", "poke", AbilityTarget.Of("target"));
        Assert.False(outcome.Ok);
        Assert.Contains("range", outcome.Message);
    }

    // ---- Equipment (server-side; over Inventory + Stats) ----

    [Fact]
    public async Task Equipment_Equip_AppliesItemStats_AndRespectsSlotRules()
    {
        var server = new TestServer(Config(6103));
        var inv = server.UseInventory();
        var stats = StatSchema.Create().Define("attack_power", 5, min: 0).Build().NewSet();

        var schema = EquipmentSchema.Create()
            .Slot("weapon", id => id.StartsWith("weapon"))
            .Slot("head")
            .Build();
        var equip = server.UseEquipment(inv, new EquipmentOptions
        {
            Schema = schema,
            StatsOf = _ => stats,
            ItemStats = id => id == "weapon_sword" ? new[] { StatModifier.Flat("attack_power", 10) } : null,
        });

        await inv.GrantAsync("p1", "weapon_sword", 1);
        var (ok, _) = await equip.EquipAsync("p1", "weapon", "weapon_sword");
        Assert.True(ok);
        Assert.Equal(15, stats.Get("attack_power"), 3);                       // 5 + 10 from the sword
        Assert.Equal("weapon_sword", equip.GetEquipped("p1")["weapon"]);
        Assert.DoesNotContain((await inv.GetAsync("p1")), s => s.ItemId == "weapon_sword");   // moved out of the bag

        // A non-weapon can't go in the weapon slot.
        await inv.GrantAsync("p1", "potion", 1);
        var (bad, msg) = await equip.EquipAsync("p1", "weapon", "potion");
        Assert.False(bad);
        Assert.Contains("fit", msg);

        // Unequip reverts the stat and returns the item.
        await equip.UnequipAsync("p1", "weapon");
        Assert.Equal(5, stats.Get("attack_power"), 3);
        Assert.Contains((await inv.GetAsync("p1")), s => s.ItemId == "weapon_sword");
    }

    // ---- StateMachine ----

    [Fact]
    public void StateMachine_TransitionsOnGuards_AndFiresEnter()
    {
        var ctx = new FsmCtx();
        var fsm = StateMachine<FsmCtx>.Build()
            .State("idle")
            .State("chase", onEnter: c => c.ChaseEnters++)
            .Transition("idle", "chase", c => c.SeesPlayer)
            .Transition("chase", "idle", c => !c.SeesPlayer)
            .Create();
        fsm.Start("idle", ctx);
        Assert.Equal("idle", fsm.CurrentName);

        ctx.SeesPlayer = true;
        fsm.Update(ctx, 16);
        Assert.Equal("chase", fsm.CurrentName);
        Assert.Equal(1, ctx.ChaseEnters);

        ctx.SeesPlayer = false;
        fsm.Update(ctx, 16);
        Assert.Equal("idle", fsm.CurrentName);
    }

    private sealed class FsmCtx { public bool SeesPlayer; public int ChaseEnters; }

    // ---- BehaviorTree ----

    [Fact]
    public void BehaviorTree_SelectorAndSequence_Work()
    {
        var ctx = new BtCtx();
        var bt = BehaviorTree<BtCtx>.Build()
            .Selector()
                .Sequence()
                    .Condition(c => c.HasTarget)
                    .Do(c => c.Attacked = true)
                .End()
                .Do(c => c.Idled = true)
            .End()
            .Create();

        // No target → first sequence fails at the condition → falls through to idle.
        Assert.Equal(BtStatus.Success, bt.Tick(ctx, 16));
        Assert.True(ctx.Idled);
        Assert.False(ctx.Attacked);

        // With a target → the sequence runs the attack.
        ctx.Idled = false; ctx.HasTarget = true;
        Assert.Equal(BtStatus.Success, bt.Tick(ctx, 16));
        Assert.True(ctx.Attacked);
        Assert.False(ctx.Idled);
    }

    [Fact]
    public void BehaviorTree_Wait_StaysRunningThenSucceeds()
    {
        var bt = BehaviorTree<BtCtx>.Build().Sequence().Wait(100).Do(c => c.Attacked = true).End().Create();
        var ctx = new BtCtx();
        Assert.Equal(BtStatus.Running, bt.Tick(ctx, 50));
        Assert.False(ctx.Attacked);
        Assert.Equal(BtStatus.Success, bt.Tick(ctx, 60));   // 110ms ≥ 100 → wait done, then Do
        Assert.True(ctx.Attacked);
    }

    private sealed class BtCtx { public bool HasTarget; public bool Attacked; public bool Idled; }

    // ---- Spawning (over Mobs, headless) ----

    [Fact]
    public void Spawning_FillsZone_AndRespawnsAfterDeath()
    {
        var server = new TestServer(Config(6104));
        var mobs = server.UseMobs(new MobOptions { UseInternalTimer = false, AllPlayers = () => Array.Empty<string>() });
        mobs.Register(new AggressiveBrain("wolf"));
        var spawning = server.UseSpawning(mobs, new SpawnOptions { UseInternalTimer = false });
        spawning.AddZone(SpawnZone.Circle("forest", new Vec3(0, 0, 0), 10).Add("wolf", count: 3, respawnMs: 100));

        spawning.Update(1000);
        Assert.Equal(3, mobs.Mobs.Count(m => m.IsAlive));   // filled to target

        mobs.Mobs.First(m => m.IsAlive).Health = 0;         // kill one
        spawning.Update(50);
        Assert.Equal(2, mobs.Mobs.Count(m => m.IsAlive));   // death detected, not yet respawned
        spawning.Update(100);
        Assert.Equal(3, mobs.Mobs.Count(m => m.IsAlive));   // respawn delay elapsed → back to target
    }

    // ---- Persistence ----

    [Fact]
    public async Task Persistence_JsonFileStore_RoundTripsAcrossInstances()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "setnet_persist_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonFileDocumentStore<SaveBlob>(path);
            await store.SetAsync("p1", new SaveBlob { Level = 7, Name = "Aria" });
            Assert.True(await store.ExistsAsync("p1"));

            var reopened = new JsonFileDocumentStore<SaveBlob>(path);   // reads from disk
            var loaded = await reopened.GetAsync("p1");
            Assert.NotNull(loaded);
            Assert.Equal(7, loaded!.Level);
            Assert.Equal("Aria", loaded.Name);
            Assert.Single(await reopened.KeysAsync());
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    public sealed class SaveBlob { public int Level { get; set; } public string Name { get; set; } = ""; }

    [Fact]
    public async Task Persistence_SnapshotStore_RoundTrips()
    {
        ISnapshotStore store = new MemorySnapshotStore();
        await store.SaveAsync("world", new byte[] { 1, 2, 3, 4 });
        var back = await store.LoadAsync("world");
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, back);
        Assert.True(await store.DeleteAsync("world"));
        Assert.Null(await store.LoadAsync("world"));
    }

    // ---- Docs ----

    [Fact]
    public void Docs_DiscoversChannelsAndHandlers()
    {
        _ = typeof(DialogueChannelService);   // ensure the Dialogue assembly is loaded
        var report = ProtocolDocs.Generate();
        Assert.NotEmpty(report.Channels);

        var dialogue = report.Channels.FirstOrDefault(c => c.Channel == 31);
        Assert.NotNull(dialogue);
        Assert.Equal("Dialogue", dialogue!.Name);
        Assert.Contains("DialogueChannelService", dialogue.Services);

        var md = report.ToMarkdown();
        Assert.Contains("Unified protocol channels", md);
    }

    private static Configuration Config(int port) => new Configuration { Host = "127.0.0.1", Port = port, TransportType = TransportType.Tcp };
}

/// <summary>End-to-end smokes for the networked gameplay modules (Dialogue, Notifications) over the unified protocol.</summary>
[Collection("integration")]
public class GameplayWireTests
{
    private static Configuration Config(int port) => new Configuration { Host = "127.0.0.1", Port = port, TransportType = TransportType.Tcp };

    [Fact]
    public async Task Dialogue_WalksBranchesToEnd()
    {
        DialogueRuntime.Enable();
        var server = new TestServer(Config(6111));
        server.UseDialogue().Define("greeter", DialogueTree.Create()
            .Node("start", "Hello, traveller!",
                new DialogueChoice("Who are you?", "who"),
                new DialogueChoice("Goodbye.", null))
            .Node("who", "I am the gatekeeper.",
                new DialogueChoice("I see. Farewell.", null))
            .Build());
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(Config(6111));
        var dialogue = client.UseDialogue();
        await client.ConnectAsync();

        var start = await dialogue.StartAsync("greeter");
        Assert.Equal("Hello, traveller!", start.Text);
        Assert.Equal(2, start.Choices.Count);

        var who = await dialogue.ChooseAsync(0);        // "Who are you?"
        Assert.Equal("I am the gatekeeper.", who.Text);
        Assert.False(who.IsEnd);

        var end = await dialogue.ChooseAsync(0);         // "Farewell." → null → end
        Assert.True(end.IsEnd);

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Notifications_Broadcast_ReachesClient()
    {
        var server = new TestServer(Config(6112));
        var notifications = server.UseNotifications();
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(Config(6112));
        var received = new TaskCompletionSource<Notification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nc = client.UseNotifications();
        nc.Received += n => received.TrySetResult(n);
        await client.ConnectAsync();
        await Task.Delay(150);   // let the server register the peer

        await notifications.BroadcastAsync(new Notification("achievement", "First Blood", "You defeated a mob."));

        var got = await Task.WhenAny(received.Task, Task.Delay(3000)) == received.Task ? received.Task.Result : null;
        Assert.NotNull(got);
        Assert.Equal("First Blood", got!.Title);
        Assert.Equal("achievement", got.Kind);

        client.Disconnect();
        await server.StopAsync();
    }
}
