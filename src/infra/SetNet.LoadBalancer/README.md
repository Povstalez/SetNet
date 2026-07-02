<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.LoadBalancer

**Least-loaded node selection for [SetNet](https://www.nuget.org/packages/SetNet) clusters.**

A well-known entry node keeps a registry of game nodes with their reported load and capacity; a client asks for the emptiest node with room and connects there. It's the capacity-driven counterpart to [`SetNet.Sharding`](https://www.nuget.org/packages/SetNet.Sharding) (which routes by *key*): use the balancer when any node will do and you just want the least busy one. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.LoadBalancer
```

## Usage

Call `LoadBalancerRuntime.Enable()` once at startup on both ends.

```csharp
// entry-node server — feed it node loads (from SetNet.Cluster gossip, an orchestrator, or health pings)
LoadBalancerRuntime.Enable();
var lb = server.UseLoadBalancer();
lb.UpdateNode(new LbNode("eu-1", "eu1.game.example", 5000, load: 0, capacity: 500));
lb.UpdateNode(new LbNode("eu-2", "eu2.game.example", 5000, load: 0, capacity: 500));
// as loads change:
lb.ReportLoad("eu-1", currentPlayers);

// client — pick the emptiest node, then connect there
LoadBalancerRuntime.Enable();
var lb = entryClient.UseLoadBalancer();
LbNode node = await lb.PickAsync();
var gameClient = new GameClient(new Configuration { Host = node.Host, Port = node.Port /* ... */ });
await gameClient.ConnectAsync();
```

## API

**Server:** `server.UseLoadBalancer()` → `LoadBalancerServer` — `UpdateNode(LbNode)`, `ReportLoad(nodeId, load)`, `RemoveNode(nodeId)`, `Pick()`, `Nodes`.
**Client:** `client.UseLoadBalancer()` → `LoadBalancerClient` — `PickAsync()` (throws when all nodes are full).

`LoadBalancerRuntime.Enable()` — one-time bootstrap.

## Notes

- **Reserved wire types 65454 / 65455.** Don't reuse them.
- **You feed the registry.** This package doesn't gossip or health-check on its own — push node loads via `UpdateNode`/`ReportLoad`, typically from [`SetNet.Cluster`](https://www.nuget.org/packages/SetNet.Cluster) broadcasts or your orchestrator. `RemoveNode` a drained/dead node.
- **Selection:** the node with the lowest load/capacity ratio that isn't full. Uncapped nodes (capacity 0) are ranked by raw load and never reported full.
- **Balancer vs [Sharding](https://www.nuget.org/packages/SetNet.Sharding):** balancer picks by *capacity* (any node will do); Sharding picks by *key* (a room/region must always land on the same node). Use both — balance new sessions, shard keyed ones.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
