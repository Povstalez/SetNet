# SetNet.LoginServer

An **L2-style login coordinator** for SetNet: a dedicated login node authenticates the account, advertises the **game-server list**, and issues a **one-time session token** the client presents to the chosen game server — which validates it against a **shared token store**.

```
client ──login──▶ LoginServer ──(verify)──▶ your accounts
client ──list───▶ LoginServer                (server list, fed from LoadBalancer/config)
client ─select──▶ LoginServer ── issues token into shared store ── returns token + host:port
client ───────────────────────▶ GameServer ── consumes the token from the shared store ──▶ enter world
```

## Login node

```csharp
using SetNet.LoginServer;

var tokens = new MemoryLoginTokenStore();        // ← a shared Redis/DB store in a real cluster

loginNode.UseLoginServer(new LoginOptions
{
    Authenticate = async (user, pass) =>         // wire to SetNet.Accounts (or anything)
    {
        var r = await accounts.AuthenticateAsync(user, pass);
        return r.Status switch
        {
            AccountAuthStatus.Ok      => LoginAuth.Success(r.Account!.Id),
            AccountAuthStatus.Banned  => LoginAuth.Ban(r.Account!.Id, "banned"),
            _                         => LoginAuth.Reject("invalid credentials"),
        };
    },
    Servers = () => new[]                          // feed from SetNet.LoadBalancer / your config
    {
        new GameServerInfo { Id = "s1", Name = "Bartz", Host = "game1.example.com", Port = 7777, Online = 1200, Max = 2000 },
    },
    Tokens = tokens,
});
LoginRuntime.Enable();
```

## Client

```csharp
var login = client.UseLogin();
var r = await login.LoginAsync("alice", "secret");
if (!r.Ok) { /* r.Status: InvalidCredentials / Banned / ServerError */ }

var servers = await login.ServerListAsync();
var sel = await login.SelectServerAsync(servers[0].Id);   // one-time token + where to connect
// now connect to sel.Host:sel.Port and present sel.Token in your game-server handshake
```

## Game server

The game node validates the token against the **same** `ILoginTokenStore`:

```csharp
var binding = await tokens.ConsumeAsync(presentedToken);   // one-time; null if invalid/expired/used
if (binding != null) SpawnPlayer(binding.AccountId);
```

`ILoginTokenStore` ships an in-process `MemoryLoginTokenStore` (co-located / tests); implement it over Redis/DB to span nodes. Tokens are one-time and TTL'd (`LoginOptions.TokenTtlSeconds`, default 60). Composition over `SetNet.Accounts` (auth) and `SetNet.LoadBalancer` (server list). Depends only on `SetNet`.

## License
MIT
