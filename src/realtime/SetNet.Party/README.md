# SetNet.Party

**Party / group system for [SetNet](https://www.nuget.org/packages/SetNet).**

Create or join a **party** by code, track a **leader** and per-member **ready** state, and get join/leave/leader/ready events — so friends stick together and queue for matchmaking as a group.

```csharp
PartyRuntime.Enable();   // startup, both ends

server.UseParties();

var party = client.UseParty();
party.PlayerJoined  += id => ...;
party.LeaderChanged += id => ...;
party.ReadyChanged  += (id, ready) => ...;

var info = await party.CreateAsync();     // you're the leader; share info.Code
await party.SetReadyAsync(true);
// a friend:  await party.JoinAsync(code);
```

The leader is the creator; if they leave, the next member is promoted automatically. Pairs naturally with [`SetNet.Matchmaking`](https://www.nuget.org/packages/SetNet.Matchmaking) (enqueue the whole party once everyone's ready).

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
