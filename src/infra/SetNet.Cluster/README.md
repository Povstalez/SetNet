<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Cluster

**Server-to-server broadcast bus for [SetNet](https://www.nuget.org/packages/SetNet) — scale out across nodes.**

When one game/app server isn't enough, run several and connect them into a **mesh**. Each `ClusterNode` runs its own dedicated listener and dials the other nodes, forming a full-mesh bus that carries **only** cluster traffic — separate from your player-facing server. Use it to fan cross-node events out to every node: a global chat line, a "player X came online" presence update, a cache invalidation, a "shut down for maintenance" signal. Publish with `Publish<T>`, react with `Received` / `On<T>`.

This is a lightweight **broadcast bus**, not a consensus or state-replication system — think Redis pub/sub between your nodes, not Raft.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Cluster
```

## Usage

Give every node a unique id, a listen port, and the addresses of the other nodes as seeds. The mesh is symmetric — it doesn't matter who dials whom.

```csharp
// Node "eu-1"
var node = new ClusterNode(new ClusterNodeOptions
{
    NodeId = "eu-1",
    ListenPort = 7000,
    Seeds = new[] { ("10.0.0.2", 7000), ("10.0.0.3", 7000) },  // the other nodes
});

node.Received += (fromNode, topic, body) =>
    Console.WriteLine($"{fromNode} → {topic} ({body.Length} bytes)");

// Strongly-typed subscription (body deserialized via SetNetSerializer):
node.On<ChatLine>("global-chat", (fromNode, line) =>
    BroadcastToLocalPlayers(line));

await node.StartAsync();

// Fan out to every other node:
await node.Publish("global-chat", new ChatLine { User = "alice", Text = "hi all" });
```

Typically each of your player servers *also* hosts a `ClusterNode` on a separate port; when a message arrives from another node you rebroadcast it to the players connected locally.

## API

**`new ClusterNode(ClusterNodeOptions)`**

| `ClusterNodeOptions` | Meaning |
|---|---|
| `string NodeId` | this node's identity, sent with every message |
| `int ListenPort` / `string ListenHost` | where this node accepts other nodes (`ListenHost` default `0.0.0.0`) |
| `IReadOnlyList<(string Host, int Port)> Seeds` | the other nodes to dial and keep connected |
| `int ReconnectDelayMs` | delay between reconnect attempts to a seed (default 2000) |

**`ClusterNode`**

| Member | Purpose |
|---|---|
| `Task StartAsync()` | start the listener and dial all seeds (auto-reconnecting) |
| `Task Publish(string topic, byte[] body)` | broadcast raw bytes to all connected nodes |
| `Task Publish<T>(string topic, T message)` | broadcast a serializable message |
| `void On<T>(string topic, Action<string,T> handler)` | typed per-topic subscription |
| `event Action<string,string,byte[]> Received` | raw catch-all: `(fromNodeId, topic, body)` |
| `Task Stop()` | stop the listener and outbound links |
| `string NodeId` | this node's id |

## Notes

- **Reserved wire type 65501.** Cluster traffic is isolated on its own listener/port, so it never collides with your application handlers.
- **Full mesh.** Every node connects to every other node; messages are delivered once per link. For N nodes that's N·(N-1)/2 links — fine for a handful of nodes, not for hundreds. For large fan-out put a real message broker (NATS/Redis) behind a thin adapter instead.
- **Best-effort broadcast.** Delivery is reliable *per live link*, but a node that's down when you publish simply misses the message (it reconnects afterward). There's no store-and-forward, ordering across nodes, or dedup — add a message id/version in your payload if you need idempotency.
- **Secure the mesh.** Cluster links are plain SetNet connections. Run them on a private network/VPC, or enable TLS on the node configs, and don't expose the cluster port publicly.
- **Symmetric seeds are fine.** Listing a node that also lists you just means both try to dial; duplicate links are harmless (each carries the same broadcast).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
