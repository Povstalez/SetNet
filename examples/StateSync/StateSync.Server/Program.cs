// StateSync bouncing-balls server (headless).
//   dotnet run --project examples/StateSync.Server -- [host] [port] [ballCount]
// Defaults: 127.0.0.1 5000 8. Press Enter to stop.
//
// Spawns N server-owned balls bouncing in a ±10 cube and replicates their positions to every client.
// The game just mutates entity fields each frame; the StateSync tick samples them and sends deltas.

using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.StateSync;
using StateSync.Server;
using StateSync.Shared;

SetNetSerializer.Use(new MessagePackNetSerializer());
StateSyncRuntime.Enable();
World.Register();                                       // identical schema on both ends

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 5000;
var count = args.Length > 2 && int.TryParse(args[2], out var c) ? c : 8;

var server = new BallServer(new Configuration { Host = host, Port = port, HeartbeatEnabled = true });
var world = server.UseStateSync(new StateSyncOptions { TickRate = 30 });   // 30 snapshots/sec
_ = server.StartAsync();

var rng = new Random();
var balls = new List<Ball>();
for (var i = 0; i < count; i++)
    balls.Add(new Ball(world.Spawn(World.Ball), rng));   // server-owned (no owner) → clients just observe

Console.WriteLine($"Ball server listening on {host}:{port} with {count} balls. Press Enter to stop.");

// Simulation loop: move the balls; the StateSync tick replicates whatever the fields currently hold.
using var stop = new CancellationTokenSource();
var sim = Task.Run(async () =>
{
    const float dt = 1f / 60f;
    while (!stop.IsCancellationRequested)
    {
        foreach (var b in balls) b.Step(dt);
        await Task.Delay(16);
    }
});

Console.ReadLine();
stop.Cancel();
await server.StopAsync();
Console.WriteLine("Server stopped.");
