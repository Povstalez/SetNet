// Economy / world client entry point. Run TWO of these against one server to see the drop broadcast:
//   dotnet run --project examples/Economy.Client -- create          (host: makes a room, prints its code)
//   dotnet run --project examples/Economy.Client -- join <code>     (guest: joins that room)
// Then type an item name + Enter to drop 1 (starter kit: 5x sword). The OTHER players in the room see it. '/quit' exits.

using Economy.Client;
using Economy.Shared;
using SetNet.Config;
using SetNet.Inventory;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Protocol;
using SetNet.Rooms;

SetNetSerializer.Use(new MessagePackNetSerializer());
RoomsRuntime.Enable();
InventoryRuntime.Enable();

var mode = args.Length > 0 ? args[0] : "create";
var joinCode = args.Length > 1 ? args[1] : "";

var client = new GameClient(new Configuration { Host = "127.0.0.1", Port = 5200 });
var rooms = client.UseRooms();
var inv = client.UseInventory();

// See my own inventory change (the server pushes it after a drop) …
inv.Changed += stacks =>
    Console.WriteLine("[my inventory] " + string.Join(", ", stacks.Select(s => $"{s.Count}x{s.ItemId}")));

// … and see items other players in my room drop.
client.On<ItemDropped>(WorldChannel.Id, (ushort)WorldEvt.ItemDropped,
    e => Console.WriteLine($"* {e.PlayerId} dropped {e.Count}x{e.ItemId} nearby"));

await client.ConnectAsync();

if (mode == "join" && joinCode.Length > 0)
{
    var room = await rooms.JoinAsync(joinCode);
    Console.WriteLine($"joined room {room.Code}");
}
else
{
    var room = await rooms.CreateAsync(new RoomOptions { MaxPlayers = 4 });
    Console.WriteLine($"created room — share this code: {room.Code}");
}

Console.WriteLine("Type an item name to drop it (you start with 5x sword). '/quit' to exit.");
while (true)
{
    var line = Console.ReadLine();
    if (line is null || line == "/quit") break;
    if (line.Trim().Length == 0) continue;
    await client.PostAsync(WorldChannel.Id, (ushort)WorldOp.Drop, new DropReq { ItemId = line.Trim(), Count = 1 });
}

client.Disconnect();
Console.WriteLine("Bye.");
