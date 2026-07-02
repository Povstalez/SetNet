// World — a server-side demo of SetNet.GeoData + SetNet.PathFinding + SetNet.Mobs, with NO networking and NO StateSync.
//
//   dotnet run --project examples/World -- [floors|chase|bench|all]
//
// Three self-contained demos:
//   floors  — a multi-storey (Lineage-2-style) LAYERED grid: two floors stacked at the same XZ joined by a staircase,
//             and A* that climbs from the ground floor to the deck above it. Proves storeys work on a grid (no nav-mesh).
//   chase   — a HEADLESS mob: an aggressive mob pathfinds around a wall to chase a scripted moving player, driven purely
//             by mobs.Update(dt). No StateSync, no clients — the whole AI runs offline behind the IMobReplication seam.
//   bench   — a micro-benchmark of the pooled, allocation-light grid pathfinder (thousands of queries on one reused
//             pathfinder), the shape you'd hit pathing every character/mob in an MMO.
//
// Defaults to "all". This file is intentionally dependency-light: just the three spatial/AI packages.

using System.Diagnostics;
using SetNet.Config;
using SetNet.GeoData;
using SetNet.Mobs;
using SetNet.PathFinding;
using World;

var which = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

if (which is "floors" or "all") Floors();
if (which is "chase" or "all") await ChaseAsync();
if (which is "bench" or "all") Bench();
return;

// ---------------------------------------------------------------------------------------------------------------------
// DEMO 1 — multi-storey layered grid + cross-floor pathfinding.
// ---------------------------------------------------------------------------------------------------------------------
static void Floors()
{
    Console.WriteLine("== floors: multi-storey grid ==");

    // A ground room (Y=0) at x0..2, a staircase (x3=Y1, x4=Y2, x5=Y3), an upper landing (x6, Y=4), and an upper deck
    // (Y=4) spanning the whole width — so x0..5 hold TWO stacked walkable surfaces at the same XZ. This is the shape a
    // collider baker produces for a building with floors; here we hand-build it.
    var b = new LayeredGridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 7, depth: 3)
        .SetMaxStep(1.1f).SetLayerMatchTolerance(2f);
    for (var z = 0; z < 3; z++)
    {
        b.AddLayer(0, z, 0f); b.AddLayer(1, z, 0f); b.AddLayer(2, z, 0f);      // ground room
        b.AddLayer(3, z, 1f); b.AddLayer(4, z, 2f); b.AddLayer(5, z, 3f);      // staircase
        for (var x = 0; x < 7; x++) b.AddLayer(x, z, 4f);                      // upper deck (overlaps everything)
    }
    var geo = b.Build();

    // Standing at the SAME XZ, the height you pass in decides which storey you're on:
    Console.WriteLine($"  height under (0.5, y=0.1): {geo.SampleHeight(new Vec3(0.5f, 0.1f, 0.5f))}   (ground)");
    Console.WriteLine($"  height under (0.5, y=3.8): {geo.SampleHeight(new Vec3(0.5f, 3.8f, 0.5f))}   (upper deck)");

    var finder = Pathfinding.For(geo);   // -> LayeredGridPathfinder
    var path = finder.FindPath(from: new Vec3(0.5f, 0f, 1.5f), to: new Vec3(0.5f, 4f, 1.5f));
    Console.WriteLine($"  path ground -> deck directly above it: {(path.IsEmpty ? "NONE" : $"{path.Waypoints.Count} waypoints, length {path.Length:F1}")}");
    foreach (var w in path.Waypoints) Console.WriteLine($"    ({w.X:F1}, {w.Y:F1}, {w.Z:F1})");
    Console.WriteLine("  (it walks out to the staircase, climbs, and doubles back along the deck — no teleporting between floors)\n");
}

// ---------------------------------------------------------------------------------------------------------------------
// DEMO 2 — headless mob AI chasing a moving player, pathfinding around a wall. No StateSync, no clients.
// ---------------------------------------------------------------------------------------------------------------------
static async Task ChaseAsync()
{
    Console.WriteLine("== chase: headless mob pathfinding around a wall ==");

    // A 20x20 field with a wall at x=10 for z=0..15 (a gap at z=16..19). The mob starts left of the wall, the player
    // roams on the right — so the mob has to route through the gap, not straight at the target.
    var grid = new GridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 20, depth: 20)
        .Fill((x, z) => x == 10 && z <= 15 ? (false, true, 0f) : (true, false, 0f))
        .Build();

    // Scripted "player" that drifts along the right side. Positions come purely through the seam — there is no client.
    var player = new Vec3(17.5f, 0f, 2.5f);
    var server = new HeadlessServer(new Configuration());
    var mobs = server.UseMobs(new MobOptions
    {
        UseInternalTimer = false,                       // we tick it ourselves
        MoveSpeed = 6f,
        GeoData = grid,
        Pathfinder = Pathfinding.For(grid),             // pooled grid A* — reused for every FindPath the AI does
        PlayerPosition = key => key == "hero" ? player : (Vec3?)null,
        AllPlayers = () => new[] { "hero" },
    });
    // Aggro across the whole map and don't require line of sight, so it commits to the chase around the wall.
    mobs.Register(new AggressiveBrain("wolf", aggroRadius: 40f, attackRange: 1.5f, leashRadius: 100f, requireLos: false));
    var id = mobs.Spawn(new MobSpawn { Type = "wolf", Position = new Vec3(2.5f, 0f, 2.5f), Health = 100 });

    for (var tick = 0; tick < 60; tick++)
    {
        player = new Vec3(17.5f, 0f, Math.Min(18.5f, 2.5f + tick * 0.25f));   // player drifts north
        await mobs.Update(100);                                                // 100 ms per tick
        var mob = mobs.Mobs.First(m => m.Id == id);
        var dist = Vec3.Distance(mob.Position, player);
        if (tick % 6 == 0 || dist < 1.6f)
            Console.WriteLine($"  t={tick,2}  mob=({mob.Position.X:F1},{mob.Position.Z:F1})  target={mob.Target ?? "-"}  dist={dist:F1}");
        if (dist < 1.6f) { Console.WriteLine("  mob reached the player.\n"); return; }
    }
    Console.WriteLine("  (chase ran to the tick limit)\n");
}

// ---------------------------------------------------------------------------------------------------------------------
// DEMO 3 — pathfinder throughput on a reused instance (allocation-light hot path).
// ---------------------------------------------------------------------------------------------------------------------
static void Bench()
{
    Console.WriteLine("== bench: pooled grid pathfinder throughput ==");

    const int size = 128;
    var rng = new Random(1234);
    // A big open field peppered with ~8% random obstacles.
    var grid = new GridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: size, depth: size)
        .Fill((x, z) => rng.NextDouble() < 0.08 ? (false, true, 0f) : (true, false, 0f))
        .Build();
    var finder = new GridPathfinder(grid);   // ONE instance, reused — its search memory is pooled, not re-allocated

    // Corner-to-corner-ish queries across the whole map.
    var from = new Vec3(1.5f, 0, 1.5f);
    var to = new Vec3(size - 1.5f, 0, size - 1.5f);

    finder.FindPath(from, to);   // warm up
    const int n = 5000;
    var sw = Stopwatch.StartNew();
    var found = 0;
    for (var i = 0; i < n; i++)
        if (!finder.FindPath(from, to).IsEmpty) found++;
    sw.Stop();

    Console.WriteLine($"  {size}x{size} grid, {n} full-map queries on one reused pathfinder:");
    Console.WriteLine($"  {sw.Elapsed.TotalMilliseconds:F0} ms total  ->  {n / sw.Elapsed.TotalSeconds:F0} paths/sec  ({sw.Elapsed.TotalMilliseconds / n:F3} ms/path), {found}/{n} reachable");
    Console.WriteLine("  (per-query arrays are pooled + generation-stamped, so cost scales with nodes *expanded*, not map size)");
}
