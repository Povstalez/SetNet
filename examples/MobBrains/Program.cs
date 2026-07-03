// MobBrains — three mobs, three AI styles, ONE tick loop, ONE movement system, ONE service locator.
//
//   • "shadow"   is driven by a SetNet.BehaviorTree (Selector: close the gap ▸ else hold at a standoff)
//   • "lunger"   is driven by a SetNet.StateMachine  (advance ⇄ hold, with hysteresis)
//   • "follower" just walks onto the player
//
// Everything is driven by a single SetNet.Ticks.TickScheduler (Locomotion @15 Hz + Mob AI @10 Hz auto-subscribe to it).
// All three mobs move THROUGH the same SetNet.Locomotion system (SetNet.Mobs.Locomotion bridge). Every brain reaches the
// (moving) player's position through a SetNet.Services ServiceHub — no brain stores a reference to it.
//
//   dotnet run --project examples/MobBrains
//
// No networking — one process so you can watch the AI. Mobs are faster than the player and start next to it, so they
// stay inside Mobs' "keep-awake" radius (Mobs sleeps unobserved mobs — that's what the PlayerPosition seam feeds).

using SetNet.Config;
using SetNet.GeoData;
using SetNet.Locomotion;
using SetNet.Mobs;
using SetNet.Mobs.Locomotion;
using SetNet.Services;
using SetNet.Ticks;
using MobBrains;

var geo = new GridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 60, depth: 60)
    .Fill((_, _) => (true, false, 0f)).Build();

// 1) ONE scheduler drives everything, ONE hub locates everything. Set both current BEFORE creating the systems,
//    so they auto-subscribe / can be resolved.
var ticks = new TickScheduler().MakeCurrent();
var hub   = new ServiceHub().MakeCurrent();

// 2) Locomotion auto-subscribes to `ticks` (channel "locomotion" @15 Hz). Stash it in the hub too.
var server = new HeadlessServer(new Configuration());
var loco = hub.Add(server.UseLocomotion(geo, new LocomotionOptions { Hz = 15 }));

// 3) The player is just a Mover on that system. Wrap it so every brain can find it via the hub.
var player = loco.CreateMover(new Vec3(30, 0, 30), speed: 4f, owner: "player");
hub.Add(new PlayerRef(player));

// 4) Mob AI: shares the SAME LocomotionSystem (so mobs "walk through Locomotion") and auto-subscribes to `ticks`
//    (channel "mobs" @10 Hz). The PlayerPosition seam lets Mobs' sleep gate keep the mobs awake near the player.
var mobs = hub.Add(server.UseMobs(new MobOptions
{
    MoveSpeed      = 6f,                                          // faster than the player → they keep up (stay awake)
    GeoData        = geo,
    Mover          = loco.AsMobMover(),                          // ← mobs move through the unified tick
    AllPlayers     = () => new[] { "player" },
    PlayerPosition = key => key == "player" ? player.Position : (Vec3?)null,
}));

var shadowBrain = new ShadowBrain();
var lungerBrain = new LungerBrain();
mobs.Register(shadowBrain);
mobs.Register(lungerBrain);
mobs.Register(new FollowerBrain());

var shadow   = mobs.Spawn(new MobSpawn { Type = "shadow",   Position = new Vec3(26, 0, 30), Health = 100 });
var lunger   = mobs.Spawn(new MobSpawn { Type = "lunger",   Position = new Vec3(34, 0, 30), Health = 100 });
var follower = mobs.Spawn(new MobSpawn { Type = "follower", Position = new Vec3(30, 0, 34), Health = 100 });

// 5) The player walks a compact loop (also advanced by the same tick).
var route = new[] { new Vec3(30, 0, 44), new Vec3(44, 0, 44), new Vec3(44, 0, 30), new Vec3(30, 0, 30) };
var leg = 0;
player.GoTo(route[leg]);
player.DestinationReached += _ => { if (++leg < route.Length) player.GoTo(route[leg]); };

Console.WriteLine("== MobBrains: BehaviorTree + StateMachine + Follower — all via Ticks + Locomotion ==\n");
Console.WriteLine("shadow (BT) trails at a ~7 standoff; lunger (FSM) advances to ~4 then holds until you pull away;");
Console.WriteLine("follower walks right onto you. Every brain reads the player via the ServiceHub — no wiring.\n");

// 6) Start the ONE scheduler. It drives Locomotion + all three brains. We only read positions and print.
ticks.Start(baseHz: 30);

MobInstance M(string id) => mobs.Mobs.First(m => m.Id == id);
float D(string id) => Vec3.Distance(M(id).Position, player.Position);
for (var s = 0; s < 26; s++)
{
    await Task.Delay(500);
    var p = player.Position;
    Console.WriteLine(
        $"t={s * 0.5,4:F1}s  player=({p.X,3:F0},{p.Z,3:F0})   " +
        $"shadow d={D(shadow),4:F1}   " +
        $"lunger d={D(lunger),4:F1} [{lungerBrain.StateOf(lunger)}]   " +
        $"follower d={D(follower),4:F1}");
}

ticks.Stop();
Console.WriteLine("\nONE TickScheduler drove Locomotion (15Hz) + all three brains (10Hz); ONE ServiceHub handed every brain");
Console.WriteLine("the player — no manual Update() calls, no subscriptions, no references threaded through constructors.");
