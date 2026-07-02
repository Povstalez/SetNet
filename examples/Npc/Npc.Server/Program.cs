// NPC town server.
//   dotnet run --project examples/Npc.Server -- [host] [port]
// Defaults: 127.0.0.1 5000. Press Enter to stop.
//
// The server registers a few NPC BEHAVIOURS (one class per NPC type) and spawns instances of them in the "town" zone.
// `server.UseNpc()` wires up zone-interest discovery + the interact request/reply; each behaviour decides what an
// interaction returns — often a CAPABILITY hand-off ("vendor:blacksmith") telling the client which domain module to
// talk to next. Custom NPCs = write your own INpcBehaviour; here we use the three that ship with SetNet.NPC.

using Npc.Server;
using Npc.Shared;
using SetNet.Config;
using SetNet.GeoData;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.NPC;

SetNetSerializer.Use(new MessagePackNetSerializer());   // core bundles no serializer
NpcRuntime.Enable();                                    // load NPC before handler discovery

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5000;

var server = new TownServer(new Configuration { Host = host, Port = port, HeartbeatEnabled = true });
var npc = server.UseNpc();

// One behaviour per NPC type. Interacting with them returns a capability the client hands off to a domain module.
npc.Register(new VendorNpcBehaviour("blacksmith"));            // -> capability "vendor:blacksmith"
npc.Register(new TeleporterNpcBehaviour("dungeon"));          // -> capability "teleport:dungeon"

// Spawn instances into the town zone (position is used for range/interest).
npc.Spawn(new NpcSpawn { Type = "vendor",     Zone = Zones.Town, Position = new Vec3(3, 0, 1), Metadata = System.Text.Encoding.UTF8.GetBytes("Borin the Blacksmith") });
npc.Spawn(new NpcSpawn { Type = "teleporter", Zone = Zones.Town, Position = new Vec3(8, 0, 5), Metadata = System.Text.Encoding.UTF8.GetBytes("Runic Gate") });

_ = server.StartAsync();
Console.WriteLine($"NPC town server listening on {host}:{port} (zone '{Zones.Town}': a blacksmith + a teleporter). Press Enter to stop.");
Console.ReadLine();

await server.StopAsync();
Console.WriteLine("Server stopped.");
