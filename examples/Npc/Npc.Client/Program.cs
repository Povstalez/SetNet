// NPC town client — walk into a zone, discover the NPCs there, interact, and follow the capability hand-off.
//
//   dotnet run --project examples/Npc.Client -- [host] [port]
//
// Shows: SetNet.NPC
//   npcs.EnterZoneAsync(zone)              -> populates npcs.Nearby (+ NpcSpawned events)
//   npcs.InteractAsync(id, action)         -> NpcResponse { Ok, Message, Capability }
// The Capability (e.g. "vendor:blacksmith") is the hand-off: the client would now open the matching domain module
// (SetNet.Vendor, SetNet.Zones, …). Here we just print it.

using Npc.Client;
using Npc.Shared;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.NPC;

SetNetSerializer.Use(new MessagePackNetSerializer());
NpcRuntime.Enable();

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5000;

var client = new TownClient(new Configuration { Host = host, Port = port, HeartbeatEnabled = true });
var npcs = client.UseNpc();
await client.ConnectAsync();

// Discover who's in town.
await npcs.EnterZoneAsync(Zones.Town);
Console.WriteLine($"Entered '{Zones.Town}'. NPCs nearby:");
foreach (var n in npcs.Nearby)
    Console.WriteLine($"  - {n.Id}  type={n.Type}  \"{System.Text.Encoding.UTF8.GetString(n.Metadata)}\"  @({n.Position.X},{n.Position.Y},{n.Position.Z})");

// Interact with each and follow the capability hand-off.
foreach (var n in npcs.Nearby.ToList())
{
    var resp = await npcs.InteractAsync(n.Id, action: "open");
    Console.WriteLine($"\ninteract {n.Type} -> Ok={resp.Ok}  Message=\"{resp.Message}\"");
    if (resp.Capability is { } cap)
    {
        var kind = cap.Split(':') is { Length: 2 } parts ? parts[0] : cap;
        Console.WriteLine($"  hand-off capability: {cap}");
        Console.WriteLine(kind switch
        {
            "vendor"   => "  -> client would now open SetNet.Vendor and call ListAsync/BuyAsync on this vendor.",
            "teleport" => "  -> client would now confirm the teleport via SetNet.Zones.",
            _          => "  -> client routes this capability to the matching domain module.",
        });
    }
}

Console.WriteLine("\nDone.");
try { client.Disconnect(); } catch { /* ignore */ }
