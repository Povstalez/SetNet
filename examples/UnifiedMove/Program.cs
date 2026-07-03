// UnifiedMove — a player character and a mob move through the SAME SetNet.Locomotion tick, and the ONE `Started` hook
// fires for both (that's the moment you'd send just the destination point to clients, L2-style). The mob (SetNet.Mobs
// + SetNet.Mobs.Locomotion) chases the player, and its movement is advanced by the same LocomotionSystem as the player.
//
//   dotnet run --project examples/UnifiedMove
//
// No networking — everything ticks in one process so you can watch the unified movement. The `Started` lines are what
// a real server would broadcast to nearby clients; a Unity client then re-paths from that point and drives a NavAgent
// (see the README).

using SetNet.Config;
using SetNet.GeoData;
using SetNet.Locomotion;
using SetNet.Mobs;
using SetNet.Mobs.Locomotion;
using UnifiedMove;

// A 40x40 field with a wall at x=20 (gap near the top) so paths have to detour — proves real pathfinding for both.
var geo = new GridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 40, depth: 40)
    .Fill((x, z) => x == 20 && z < 32 ? (false, true, 0f) : (true, false, 0f))
    .Build();

// ONE unified movement system. We tick it ourselves.
var loco = new LocomotionSystem(geo, new LocomotionOptions { UseInternalTimer = false });

// The player character is just a Mover on that system. Its Owner tags who it is.
var player = loco.CreateMover(geo.SampleNearestWalkable(new Vec3(4, 0, 4)), speed: 6f, owner: "player");

// The mob AI (SetNet.Mobs) shares the SAME LocomotionSystem via the bridge — so mob movement is advanced by `loco` too.
var server = new HeadlessServer(new Configuration());
var mobs = server.UseMobs(new MobOptions
{
    UseInternalTimer = false,
    MoveSpeed = 4f,
    GeoData = geo,
    Mover = loco.AsMobMover(),                       // ← mobs move through the unified tick
    PlayerPosition = key => key == "player" ? player.Position : (Vec3?)null,   // mob perceives the player MOVER
    AllPlayers = () => new[] { "player" },
});
mobs.Register(new AggressiveBrain("wolf", aggroRadius: 60f, attackRange: 1.5f, leashRadius: 100f, requireLos: false));
var mobId = mobs.Spawn(new MobSpawn { Type = "wolf", Position = geo.SampleNearestWalkable(new Vec3(36, 0, 4)), Health = 100 });

// The ONE hook that fires for BOTH the player and the mob whenever either gets a new destination. In a networked game
// THIS is the single place you'd send just the point to nearby clients (they re-path locally). We print the player's
// (the "click") and just COUNT the mob's — because the mob re-targets the moving player every tick (see the note).
var mobMoveEvents = 0;
loco.Started += m =>
{
    if (m.Owner is string)
        Console.WriteLine($"    [SEND→clients] {Describe(m.Owner)} → move to ({m.Destination!.Value.X:F1}, {m.Destination!.Value.Z:F1})");
    else
        mobMoveEvents++;
};

Console.WriteLine("== UnifiedMove: player + mob in one LocomotionSystem, one Started hook ==\n");

// Simulate the player clicking a few destinations; the mob chases. Both advance in the single loco tick.
var route = new[] { new Vec3(36, 0, 36), new Vec3(4, 0, 36), new Vec3(36, 0, 4) };
var leg = 0;
player.GoTo(route[leg]);
player.DestinationReached += _ =>
{
    leg++;
    if (leg < route.Length) player.GoTo(route[leg]);
};

for (var t = 0; t < 120; t++)     // 12s @ 100ms
{
    await mobs.Update(100);       // mob AI decides (→ mob.GoTo on the shared system, firing Started)
    loco.Update(100);             // the ONE movement tick advances BOTH the player and the mob

    if (t % 6 == 0)
    {
        var mob = mobs.Mobs.First(x => x.Id == mobId);
        var dist = Vec3.Distance(player.Position, mob.Position);
        Console.WriteLine($"t={t,3}  player=({player.Position.X,4:F1},{player.Position.Z,4:F1})  " +
                          $"mob=({mob.Position.X,4:F1},{mob.Position.Z,4:F1})  chasing={mob.Target ?? "-"}  dist={dist:F1}  mobMoveEvents={mobMoveEvents}");
    }
    if (leg >= route.Length && !player.IsMoving) break;
}

Console.WriteLine("\nBoth the player and the mob shared ONE LocomotionSystem + ONE Started hook — that's the unification.");
Console.WriteLine("Note: the mob fired a move event nearly every tick because it re-targets the moving player. A real");
Console.WriteLine("L2-style server THROTTLES this — re-path only when the target has moved past a threshold.");

static string Describe(object? owner) => owner switch
{
    string s => $"player '{s}'",
    MobInstance mob => $"mob '{mob.Id}'",
    _ => "entity",
};
