// LoginFlow — the full L2-style entry flow in one process:
//
//   client ──login──▶ LoginServer ──verify──▶ SetNet.Accounts
//   client ──list───▶ LoginServer            (game-server list)
//   client ─select──▶ LoginServer ── issues one-time token ──▶ shared token store
//   [game] ── consumes token ──▶ SetNet.CharacterStore ── character select ──▶ enter world (+ SetNet.GameData)
//
//   dotnet run --project examples/LoginFlow
//
// The client talks to the login node over a real (in-memory) SetNet connection. The "game server" side (token
// validation + character select) runs in-process here for clarity — in a real deployment the client would connect to
// the game node at the advertised host:port and the game's handshake handler would run exactly these calls.

using SetNet.Accounts;
using SetNet.CharacterStore;
using SetNet.Config;
using SetNet.GameData;
using SetNet.InMemory;
using SetNet.LoginServer;
using SetNet.MessagePack;
using SetNet.Messaging;
using SetNet.Persistence;
using LoginFlow;

SetNetSerializer.Use(new MessagePackNetSerializer());   // the wire needs a serializer registered
LoginRuntime.Enable();

Console.WriteLine("== LoginFlow: account → login → server list → token → character select → world ==\n");

// ---- Back-end services (memory stores here; swap for SetNet.Persistence.Postgres/EfCore/… in production) ----
var accounts = new AccountServer<GameAccount>(new MemoryDocumentStore<GameAccount>(), new MemoryDocumentStore<string>());
var characters = new CharacterServer<GameCharacter>(new MemoryDocumentStore<GameCharacter>(), new CharacterOptions { MaxPerAccount = 7 });
var tokens = new MemoryLoginTokenStore();               // shared between login node and game server

var gameData = new GameDataRegistry();
var items = gameData.LoadJson<int, ItemRow>("items",
    "[{\"Id\":1,\"Name\":\"Short Sword\",\"Grade\":1},{\"Id\":57,\"Name\":\"Adena\",\"Grade\":0}]", r => r.Id);

// Seed one account.
var alice = await accounts.RegisterAsync("alice", "secret");
Console.WriteLine($"[seed] account 'alice' created (id {alice.Id[..8]}…)\n");

// ---- Login node (a real server the client connects to) ----
var loginNode = new Node(new Configuration { Host = "login", Port = 1 }.UseInMemory());
loginNode.UseLoginServer(new LoginOptions
{
    Authenticate = async (user, pass) =>
    {
        var r = await accounts.AuthenticateAsync(user, pass);
        return r.Status switch
        {
            AccountAuthStatus.Ok     => LoginAuth.Success(r.Account!.Id),
            AccountAuthStatus.Banned => LoginAuth.Ban(r.Account!.Id, "account banned"),
            _                        => LoginAuth.Reject("invalid credentials"),
        };
    },
    Servers = () => new[]
    {
        new GameServerInfo { Id = "bartz", Name = "Bartz", Host = "game1.example.com", Port = 7777, Online = 1240, Max = 2000, Status = "good" },
        new GameServerInfo { Id = "sieghardt", Name = "Sieghardt", Host = "game2.example.com", Port = 7777, Online = 1990, Max = 2000, Status = "busy" },
    },
    Tokens = tokens,
});
_ = loginNode.StartAsync();
await Task.Delay(150);

// ---- Client: log in, pick a server ----
var client = new DemoClient(new Configuration { Host = "login", Port = 1 }.UseInMemory());
var login = client.UseLogin();
await client.ConnectAsync();

Console.WriteLine("client: login as alice / wrongpass →");
Console.WriteLine($"    {(await login.LoginAsync("alice", "wrongpass")).Status}\n");

Console.WriteLine("client: login as alice / secret →");
var res = await login.LoginAsync("alice", "secret");
Console.WriteLine($"    {res.Status}\n");

Console.WriteLine("client: server list →");
var servers = await login.ServerListAsync();
foreach (var s in servers) Console.WriteLine($"    [{s.Id}] {s.Name,-10} {s.Online}/{s.Max}  ({s.Status})  {s.Host}:{s.Port}");

Console.WriteLine("\nclient: select 'bartz' →");
var sel = await login.SelectServerAsync("bartz");
Console.WriteLine($"    ok={sel.Ok}  connect to {sel.Host}:{sel.Port}  token={sel.Token[..8]}…\n");

// ---- Game server side (in-process here): validate token, character select ----
Console.WriteLine($"[game] client connects to {sel.Host}:{sel.Port} and presents the token…");
var binding = await tokens.ConsumeAsync(sel.Token);       // one-time
if (binding is null) { Console.WriteLine("[game] token invalid — rejected"); return; }
Console.WriteLine($"[game] token valid → account {binding.AccountId[..8]}… for server '{binding.ServerId}'\n");

var roster = await characters.ListAsync(binding.AccountId);
if (roster.Count == 0)
{
    Console.WriteLine("[game] no characters — creating a starter…");
    await characters.CreateAsync(binding.AccountId, new GameCharacter
    {
        Name = "Archer", Slot = 0, ClassId = 5, VipUntil = DateTime.UtcNow.AddDays(30),
    });
    roster = await characters.ListAsync(binding.AccountId);
}

Console.WriteLine("[game] character select:");
foreach (var c in roster)
    Console.WriteLine($"    slot {c.Slot}: {c.Name,-10} class={c.ClassId}  VIP until {(c.VipUntil?.ToString("yyyy-MM-dd") ?? "-")}");

var chosen = roster[0];
Console.WriteLine($"\n[game] ENTER WORLD as '{chosen.Name}'.");
Console.WriteLine($"[game] starter item from GameData: {items.Get(1)!.Name} (grade {items.Get(1)!.Grade}).");

Console.WriteLine("\nDone: Accounts + LoginServer (over the wire) + CharacterStore + GameData, end to end.");

// ---- App types with custom fields (no schema change needed) ----
sealed class GameAccount : AccountBase { }

sealed class GameCharacter : CharacterBase
{
    public int ClassId { get; set; }
    public DateTime? VipUntil { get; set; }        // ← per-character VIP, a custom field
}

// A GameData row — any custom columns you like.
sealed class ItemRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Grade { get; set; }
}
