// StateSync bouncing-balls client — connects and prints the replicated ball positions.
//   dotnet run --project examples/StateSync.Client -- [host] [port]
// Defaults: 127.0.0.1 5000. Press Enter to stop.
//
// Shows the full StateSync receive side: EntitySpawned/EntityDespawned as balls enter/leave view, and
// reading interpolated positions each frame after ClientReplication.Update().

using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.StateSync;
using StateSync.Client;
using StateSync.Shared;

SetNetSerializer.Use(new MessagePackNetSerializer());
StateSyncRuntime.Enable();
World.Register();                                       // identical schema on both ends

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 5000;

var client = new BallClient(new Configuration { Host = host, Port = port, HeartbeatEnabled = true});
var repl = client.UseStateSync(new StateSyncOptions { InterpolationDelayMs = 100 });

repl.EntitySpawned   += v => Console.WriteLine($"+ ball {v.NetId} appeared");
repl.EntityDespawned += v => Console.WriteLine($"- ball {v.NetId} gone");

await client.ConnectAsync();
Console.WriteLine("Connected. Watching replicated balls (Enter to quit):");

using var stop = new CancellationTokenSource();
var render = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        repl.Update();   // advance interpolation

        var line = new System.Text.StringBuilder();
        foreach (var v in repl.Entities)
        {
            var pos = v.GetVec3(World.Position);
            line.Append($"#{v.NetId}({pos.X,6:F1},{pos.Y,6:F1},{pos.Z,6:F1})  ");
        }
        if (line.Length > 0) Console.WriteLine(line.ToString());
        await Task.Delay(500);   // print twice a second
    }
});

Console.ReadLine();
stop.Cancel();
client.Disconnect();
