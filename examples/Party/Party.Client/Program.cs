// Party client entry point. Run TWO of these against one server to see party events:
//   dotnet run --project examples/Party/Party.Client -- create          (leader: makes a party, prints its code)
//   dotnet run --project examples/Party/Party.Client -- join <code>     (member: joins that party)
//
// Then use the interactive console:
//   ready    -> mark yourself ready
//   unready  -> clear your ready flag
//   /quit    -> leave and exit
// Everyone in the party sees join / leave / leader-changed / ready-changed events.

using Party.Client;
using Party.Shared;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Party;

SetNetSerializer.Use(new MessagePackNetSerializer());
PartyRuntime.Enable();

var mode = args.Length > 0 ? args[0] : "create";
var joinCode = args.Length > 1 ? args[1] : "";

var client = new DemoClient(new Configuration { Host = "127.0.0.1", Port = PartyDemo.DefaultPort });
var party = client.UseParty();

// Party push events — every member sees these.
party.PlayerJoined  += id => Console.WriteLine($"* {id} joined the party");
party.PlayerLeft    += id => Console.WriteLine($"* {id} left the party");
party.LeaderChanged += id => Console.WriteLine($"* {id} is now the leader");
party.ReadyChanged  += (id, ready) => Console.WriteLine($"* {id} is {(ready ? "READY" : "not ready")}");
party.Disbanded     += () => Console.WriteLine("* the party was disbanded");

Console.WriteLine("Connecting to 127.0.0.1:" + PartyDemo.DefaultPort + "...");
await client.ConnectAsync();

if (mode == "join" && joinCode.Length > 0)
{
    var info = await party.JoinAsync(joinCode);
    Console.WriteLine($"Joined party {info.Code}. Members: {string.Join(", ", info.Members.Select(m => $"{m.PlayerId}(ready={m.Ready})"))}");
}
else
{
    var info = await party.CreateAsync();
    Console.WriteLine($"Created party — share this code: {info.Code}");
    Console.WriteLine($"You are the leader ({info.OwnPlayerId}).");
}

Console.WriteLine("Commands: 'ready', 'unready', '/quit'.");
while (true)
{
    var line = Console.ReadLine();
    if (line is null || line == "/quit") break;
    switch (line.Trim().ToLowerInvariant())
    {
        case "": break;
        case "ready": await party.SetReadyAsync(true); break;
        case "unready": await party.SetReadyAsync(false); break;
        default: Console.WriteLine("Unknown command. Use 'ready', 'unready', or '/quit'."); break;
    }
}

await party.LeaveAsync();
client.Disconnect();
Console.WriteLine("Bye.");
