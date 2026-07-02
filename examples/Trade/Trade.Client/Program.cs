// Trade client entry point — escrow-trade items between two players.
//
// How to run the demo:
//   1. Start the server:            dotnet run --project examples/Trade/Trade.Server
//   2. Start client A (the invitee): dotnet run --project examples/Trade/Trade.Client
//   3. Start client B (the proposer, giving A's key from the server log):
//                                     dotnet run --project examples/Trade/Trade.Client -- propose <partnerKey>
//   The <partnerKey> is the "[server] player key: ..." line the server printed when client A connected.
//
// Then, in either client, drive the two-phase escrow trade with these commands:
//   offer <item> <count>   put an item on the table (count 0 removes it) — resets ready/confirm
//   ready                  mark ready; when BOTH are ready the trade advances to confirming
//   confirm                confirm; when BOTH confirm the server swaps the items atomically
//   cancel                 cancel the trade (nothing moves)
//   /quit                  exit
//
// Typical flow once a trade is open: both sides `offer`, both `ready`, then both `confirm`.
// (Both processes are separate, so UseTrade() with no selfPlayerKey is correct — event routing is per-process.)

using SetNet.Config;
using SetNet.Inventory;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Trade;
using Trade.Client;

SetNetSerializer.Use(new MessagePackNetSerializer());
InventoryRuntime.Enable();
TradeRuntime.Enable();

// First arg may be "propose <partnerKey>": start a trade immediately with that player.
var startPropose = args.Length > 0 && args[0].Equals("propose", StringComparison.OrdinalIgnoreCase);
var partnerKey = startPropose && args.Length > 1 ? args[1] : null;

var client = new DemoClient(new Configuration { Host = "127.0.0.1", Port = 5310 });
var inv = client.UseInventory();
var trade = client.UseTrade();   // separate process per client → no selfPlayerKey needed

// Show my inventory whenever the server pushes an update (e.g. after a completed trade).
inv.Changed += stacks =>
    Console.WriteLine("[my inventory] " + string.Join(", ", stacks.Select(s => $"{s.Count}x{s.ItemId}")));

// The other player proposed a trade to me.
trade.TradeRequested += (fromKey, view) =>
    Console.WriteLine($"* trade requested by {fromKey} — offer/ready/confirm to trade, or cancel");

// Any change to the open trade (offer edited, ready/confirm toggled, phase advanced).
trade.Updated += view =>
    Console.WriteLine($"* trade {view.State}: you offer [{Describe(view.YourOffer)}] (ready={view.YouReady}, confirmed={view.YouConfirmed}) | " +
                      $"{view.PartnerKey} offers [{Describe(view.PartnerOffer)}] (ready={view.PartnerReady}, confirmed={view.PartnerConfirmed})");

// The swap happened — items have moved.
trade.Completed += view =>
    Console.WriteLine($"* trade COMPLETE — you received [{Describe(view.PartnerOffer)}]");

// The trade was cancelled (by a participant, a disconnect, or a shortfall rollback).
trade.Cancelled += reason =>
    Console.WriteLine($"* trade cancelled: {reason}");

Console.WriteLine("Connecting to 127.0.0.1:5310...");
await client.ConnectAsync();
Console.WriteLine("Connected. Commands: offer <item> <count> | ready | confirm | cancel | /quit");

if (startPropose)
{
    if (string.IsNullOrWhiteSpace(partnerKey))
        Console.WriteLine("usage: propose <partnerKey>  (copy the partner's key from the server log)");
    else
    {
        try
        {
            var id = await trade.ProposeAsync(partnerKey!);
            Console.WriteLine($"proposed trade {id} to {partnerKey}");
        }
        catch (TradeException ex) { Console.WriteLine($"propose failed: {ex.Message}"); }
    }
}
else
{
    Console.WriteLine("Waiting for a trade request (or run another client with `propose <yourKey>`).");
}

while (true)
{
    var line = Console.ReadLine();
    if (line is null || line == "/quit") break;
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0) continue;

    try
    {
        switch (parts[0].ToLowerInvariant())
        {
            case "offer" when parts.Length >= 3 && long.TryParse(parts[2], out var count):
                await trade.OfferAsync(parts[1], count);
                break;
            case "offer":
                Console.WriteLine("usage: offer <item> <count>");
                break;
            case "ready":
                await trade.SetReadyAsync(true);
                break;
            case "confirm":
                await trade.ConfirmAsync();
                break;
            case "cancel":
                await trade.CancelAsync();
                break;
            default:
                Console.WriteLine("commands: offer <item> <count> | ready | confirm | cancel | /quit");
                break;
        }
    }
    catch (TradeException ex) { Console.WriteLine($"command failed: {ex.Message}"); }
}

client.Disconnect();
Console.WriteLine("Bye.");

// Renders a list of offered stacks like "100xgold, 1xsword".
static string Describe(IReadOnlyList<ItemStack> stacks)
    => stacks.Count == 0 ? "empty" : string.Join(", ", stacks.Select(s => $"{s.Count}x{s.ItemId}"));
