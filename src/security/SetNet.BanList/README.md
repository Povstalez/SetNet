<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.BanList

**Ban enforcement for [SetNet](https://www.nuget.org/packages/SetNet).**

Drop all traffic from banned peers and kick them instantly. Ban by **IP** (default) or a **custom key** such as the authenticated account id. Pluggable store (in-process by default; supply Redis/DB to share bans across nodes and restarts). Composes with `SetNet.Auth` and `SetNet.RateLimit` (the gate is chained).

## Install & use

```bash
dotnet add package SetNet
dotnet add package SetNet.BanList
```

```csharp
// by IP (default):
var bans = server.UseBanList();
bans.Ban("203.0.113.7");                       // kicks any connected peer from that IP + blocks reconnects
bans.Ban("203.0.113.7", DateTime.UtcNow.AddHours(1));   // timed ban
bans.Unban("203.0.113.7");

// by account (pair with SetNet.Auth):
var bans = server.UseBanList(peer => AccountOf(peer));
bans.Ban("account-123");
```

Provide your own store for cross-node/persistent bans:

```csharp
server.UseBanList(new RedisBanStore());   // implements IBanStore
```

## License

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
