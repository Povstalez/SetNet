using System.Collections.Concurrent;
using System.Threading.Tasks;
using SetNet.BehaviorTree;
using SetNet.GeoData;
using SetNet.Locomotion;
using SetNet.Mobs;
using SetNet.Services;
using SetNet.StateMachine;

namespace MobBrains;

/// <summary>The player, wrapped so any class can find it through the <see cref="ServiceHub"/> without constructor plumbing.</summary>
public sealed class PlayerRef
{
    public Mover Mover { get; }
    public PlayerRef(Mover mover) => Mover = mover;
    public Vec3 Position => Mover.Position;
}

/// <summary>
/// A mob driven by a <b>BehaviorTree</b>: a Selector that chases the player when further than a standoff distance, and
/// holds (just faces) when inside it — so it shadows the player at arm's length. The tree ticks inside
/// <see cref="ThinkAsync"/>, which itself runs on the shared <c>TickScheduler</c> via <c>MobServer</c>. The player
/// position comes from the <see cref="ServiceHub"/>; the brain stores no reference to it.
/// </summary>
public sealed class ShadowBrain : MobBrain
{
    public override string MobType => "shadow";

    private readonly ConcurrentDictionary<string, Bb> _bb = new();
    private readonly BehaviorTree<Bb> _tree;

    public ShadowBrain()
    {
        _tree = BehaviorTree<Bb>.Build()
            .Selector()
                .Sequence()                                     // too far → close the gap
                    .Condition(bb => bb.DistToPlayer() > 7f)
                    .Do(bb => bb.Ctx.MoveTo(bb.Player))
                .End()
                .Do(bb => bb.Ctx.Face(bb.Player))               // within standoff → hold and watch
            .End()
            .Create();
    }

    public override void OnSpawn(MobContext ctx) => _bb[ctx.Mob.Id] = new Bb();

    public override Task ThinkAsync(MobContext ctx, MobSenses senses)
    {
        var bb = _bb[ctx.Mob.Id];
        bb.Ctx = ctx;
        _tree.Tick(bb, 100f);       // one BT tick per AI tick
        return Task.CompletedTask;
    }

    private sealed class Bb
    {
        public MobContext Ctx = null!;
        public Vec3 Player => Service.Get<PlayerRef>().Position;                 // ← via the locator
        public float DistToPlayer() => Vec3.Distance(Ctx.Mob.Position, Player);
    }
}

/// <summary>
/// A mob driven by a <b>StateMachine</b>: <c>advance</c> (walk onto the player) ⇄ <c>hold</c> (stand and face), toggled
/// by distance with hysteresis — so it lunges in, waits, then lunges again as the player pulls away. One FSM instance
/// per mob (it owns the current state). Player position via the hub.
/// </summary>
public sealed class LungerBrain : MobBrain
{
    public override string MobType => "lunger";

    private readonly ConcurrentDictionary<string, Unit> _mobs = new();

    public override void OnSpawn(MobContext ctx)
    {
        var bb = new Bb();
        var fsm = StateMachine<Bb>.Build()
            .State("advance", onUpdate: (b, _) => b.Ctx.MoveTo(b.Player))
            .State("hold",    onUpdate: (b, _) => b.Ctx.Face(b.Player))
            .Transition("advance", "hold",    b => b.DistToPlayer() < 4f)
            .Transition("hold",    "advance", b => b.DistToPlayer() > 9f)        // hysteresis
            .Create();
        fsm.Start("advance", bb);
        _mobs[ctx.Mob.Id] = new Unit { Fsm = fsm, Bb = bb };
    }

    public override Task ThinkAsync(MobContext ctx, MobSenses senses)
    {
        var u = _mobs[ctx.Mob.Id];
        u.Bb.Ctx = ctx;
        u.Fsm.Update(u.Bb, 100f);
        return Task.CompletedTask;
    }

    public string StateOf(string mobId) => _mobs.TryGetValue(mobId, out var u) ? u.Fsm.CurrentName ?? "?" : "?";

    private sealed class Unit { public StateMachine<Bb> Fsm = null!; public Bb Bb = null!; }

    private sealed class Bb
    {
        public MobContext Ctx = null!;
        public Vec3 Player => Service.Get<PlayerRef>().Position;                 // ← via the locator
        public float DistToPlayer() => Vec3.Distance(Ctx.Mob.Position, Player);
    }
}

/// <summary>The simplest brain: always walk onto the player. Position resolved from the <see cref="ServiceHub"/>.</summary>
public sealed class FollowerBrain : MobBrain
{
    public override string MobType => "follower";

    public override Task ThinkAsync(MobContext ctx, MobSenses senses)
    {
        ctx.MoveTo(Service.Get<PlayerRef>().Position);
        return Task.CompletedTask;
    }
}
