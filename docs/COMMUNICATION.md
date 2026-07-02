<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet — Communication model (all send/receive methods)

Every way a **client** talks to the **server** and the **server** talks back — the core layer, the unified
`SetNet.Protocol` layer, and the higher-level modules (RPC, StateSync, Streams, Voice, …). They all run **together on
one connection**; this page is the complete reference plus a full worked example at the end.

- [Can I use everything together?](#can-i-use-everything-together)
- [Layer 1 — Core: typed & raw one-way messages](#layer-1--core-typed--raw-one-way-messages)
- [Layer 2 — SetNet.Protocol: request/reply, fire-and-forget, push](#layer-2--setnetprotocol-requestreply-fire-and-forget-push)
- [Layer 3 — Modules](#layer-3--modules)
- [Which method should I use?](#which-method-should-i-use)
- [Direction reference](#direction-reference)
- [Worked example — “drop an item, the other 3 players see it”](#worked-example--drop-an-item-the-other-3-players-see-it)

---

## Can I use everything together?

**Yes.** `SendAsync` (core), `SetNet.Protocol` channels, `RPC`, `StateSync`, `Voice`, `Streams`, … all coexist on the
same socket, because each occupies a **distinct wire-type space** and never collides:

| Mechanism | Wire type(s) on the socket |
|---|---|
| **Your app messages** (core `SendAsync<T>`) | any `ushort` **you** pick — number them from `0` up |
| **SetNet.Protocol** (all `Channels`) | a single envelope id **65447** (demuxed internally by channel + op) |
| StateSync | 65518–65522 · StateSync.Rpc 65516 |
| Fragmentation 65517 · Multiplex 65494 · Streams 65492/65493 | (transparent / control) |
| Voice 65503–65505 · Relay 65498–65500 · Cluster 65501 | |
| RPC 65531/65532 · Auth 65529/65530 · ProofOfWork 65506/65507 | |
| Core system (heartbeat, UDP bind token) | 65533–65535 |

The only rule for **your own** core message types: keep them low (from `0`), so they never reach the reserved block
at the top. The `SetNet.Protocol` channel space is **separate** from that `ushort` type space — your app channels
(see below) don't compete with your app message types.

Typical real app mixes several at once, e.g.: **Auth** (gate) + **Rooms** (grouping) + **Inventory** (authoritative
items) + **StateSync** (positions, unreliable) + a **custom Protocol channel** (game actions) + **Voice** (chat).

---

## Layer 1 — Core: typed & raw one-way messages

The lowest level. A global `ushort` message-type space, one handler per type, fire-and-forget in both directions.
Use it for your own top-level message types, relays, or **unreliable hot-path** traffic.

### Send (client and peer are symmetric)

```csharp
// Typed — serialized with your SetNetSerializer
await client.SendAsync<MyMsg>((ushort)MyType.Move, msg);                              // default delivery
await client.SendAsync<MyMsg>((ushort)MyType.Move, msg, DeliveryMethod.Unreliable);   // pick reliability
await client.SendAsync<MyMsg>((ushort)MyType.Move, msg, DeliveryMethod.Reliable, channel: 1); // reliable-UDP lane

await peer.SendAsync<MyMsg>((ushort)MyType.State, msg);   // server → this client (same overloads)

// Raw — already-serialized bytes, no (de)serialization (relay/proxy)
await client.SendRawAsync((ushort)MyType.Move, bytes, DeliveryMethod.Reliable);
```

### Receive — auto-discovered handlers

```csharp
[MessageHandler((ushort)MyType.Move)]
public class MoveHandler : IServerMessageHandler<MyMsg>          // server side
{
    public Task HandleAsync(BasePeer peer, MyMsg m) { /* … */ return Task.CompletedTask; }
}

[MessageHandler((ushort)MyType.State)]
public class StateHandler : IClientMessageHandler<MyMsg>         // client side
{
    public Task HandleAsync(MyMsg m) { /* … */ return Task.CompletedTask; }
}
```

### Raw interception / re-injection (relays, custom transports)

```csharp
protected override bool OnRawFrame(ushort type, byte[] data) { /* inspect/forward */ return false; } // true = consume
socket.InjectFrame(type, data);   // feed a reconstructed frame into normal dispatch
```

- `DeliveryMethod`: `Reliable` (TCP, or UDP reliability layer) or `Unreliable` (raw UDP; TCP fallback in Both mode).
- The reliable-UDP `channel` (0-based) separates independent reliable streams so a stall on one doesn't delay another.

---

## Layer 2 — SetNet.Protocol: request/reply, fire-and-forget, push

The uniform layer almost every module rides. One envelope id, demultiplexed by a **channel** (`Channels.X`) and an
**op**. Three verbs, identical everywhere. Namespace `SetNet.Protocol`.

### Client → server

```csharp
using SetNet.Protocol;

// Request → reply (correlated; timeout + one shared correlation registry)
TResp resp    = await client.RequestAsync<TReq, TResp>(Channels.Rooms, (ushort)RoomOp.Create, req,
                                                       timeoutMs: 10000, ct: default);   // typed
byte[] raw    = await client.RequestRawAsync(Channels.Rooms, (ushort)RoomOp.Create, body); // raw

// Fire-and-forget (the only form where you choose reliability)
await client.PostAsync(Channels.Chat, (ushort)ChatOp.Message, dto, DeliveryMethod.Reliable);  // typed
await client.PostRawAsync(Channels.Chat, (ushort)ChatOp.Message, bytes, DeliveryMethod.Unreliable);
```

### Server → client (push)

```csharp
await peer.PublishAsync(Channels.Rooms, (ushort)RoomEvt.PlayerJoined, evt);       // one peer, typed
await peer.PublishRawAsync(Channels.Rooms, (ushort)RoomEvt.PlayerJoined, bytes);  // one peer, raw

IEnumerable<BasePeer> members = ...;
await members.PublishAsync(Channels.Rooms, (ushort)RoomEvt.Message, evt);         // many peers (best-effort)
```

### Client — receiving push (two styles, both fire)

```csharp
// (a) imperative — closes over state, returns IDisposable to unsubscribe
IDisposable sub = client.On<PlayerJoined>(Channels.Rooms, (ushort)RoomEvt.PlayerJoined, e => Handle(e));
client.OnRaw(Channels.Rooms, (ushort)RoomEvt.Message, bytes => { /* decode yourself */ });

// (b) declarative — a [ProtocolChannel] class with [Event] methods, auto-subscribed
[ProtocolChannel(Channels.Rooms)]
public sealed class RoomEvents
{
    [Event((ushort)RoomEvt.PlayerJoined)] public void OnJoin(PlayerJoined e) { /* … */ }
    [Event((ushort)RoomEvt.Message)]      public void OnMsg(byte[] raw)      { /* … */ }
}
```

Use **(a)** when the handler needs per-instance state; **(b)** for stateless/app-singleton reactions. `[Event]`
handler instances are process-wide singletons (like client `[MessageHandler]`s).

### Server — handling inbound (two styles)

```csharp
// (a) manual switch — full control (shared setup, custom routing)
[ProtocolChannel(Channels.Rooms)]
public sealed class RoomsService : IChannelService
{
    public async Task HandleAsync(ChannelRequest r)
    {
        switch ((RoomOp)r.Op)
        {
            case RoomOp.Create:
                var req = r.Read<CreateRoom>();      // typed body … or r.RawBody for bytes
                await r.ReplyAsync(new RoomInfo(...));// typed reply … or r.ReplyRawAsync(bytes)
                break;
        }
    }
}

// (b) one method per op — no switch (scales to 100 ops)
[ProtocolChannel(Channels.Rooms)]
public sealed class RoomsService
{
    [Op((ushort)RoomOp.Create)]  public RoomInfo Create(BasePeer peer, CreateRoom req) { ... return info; }
    [Op((ushort)RoomOp.Join)]    public Task<RoomInfo> Join(BasePeer peer, JoinReq req) { ... }
    [Op((ushort)RoomOp.Broadcast)] public void Broadcast(BasePeer peer, byte[] payload) { ... }  // no reply
}
```

`[Op]` binding: parameters are matched **by type** (`BasePeer`, `ChannelRequest`, `byte[]` raw body, or one typed
body); the return becomes the reply (`T`/`Task<T>` → serialized, `byte[]`/`Task<byte[]>` → raw, `void`/`Task` → none).
A class implementing `IChannelService` keeps manual control and its `[Op]` methods are ignored.

**`ChannelRequest`**: `Peer`, `Channel`, `Op`, `RawBody`, `ExpectsReply`, `Read<T>()`, `ReplyAsync<T>(T)`,
`ReplyRawAsync(byte[])`, `ReplyErrorAsync(string)`.

### Rules of the road

- **Raw vs typed.** Typed overloads serialize via `SetNetSerializer` — so `T` must be serializable by your
  serializer (with MessagePack `StandardResolver` that means `[MessagePackObject]`/`[Key]` on your DTOs; or use
  `SetNet.Json`). Raw (`*RawAsync`, `OnRaw`, `RawBody`) is serializer-agnostic.
- **Reliability.** `Request*` and `Publish*` are always **Reliable**. Only `Post*` lets you pick `Unreliable`. For
  high-frequency unreliable state, use StateSync or the core layer.
- **Errors.** A server handler `throw new ProtocolException("reason")` → the caller’s `RequestAsync` throws
  `ProtocolException` (modules re-map it to their own type, e.g. `RoomException`).
- **Channels.** Shipped modules use ids in `Channels` (1–24). For **your own** channel, pick a distinct `ushort`
  (e.g. `1000+`) — the channel space is independent of the core `ushort` message-type space.
- **Discovery.** `[ProtocolChannel]` services and `[Event]` handlers are found by scanning loaded assemblies; make
  sure your assembly is loaded before first use (your app assembly always is; module assemblies load via
  `xxxRuntime.Enable()` / `UseXxx`).

---

## Layer 3 — Modules

### Composition modules (built on Layer 2)

Rooms, Matchmaking, Party, Chat, Inventory, Wallet, Trade, Mail, Crafting, Loot, Progression, Quests, Guilds,
Auction, Vendor, Marketplace, StatusEffects, Zones, Relay, NatPunch, Sharding, LoadBalancer, Rooms.HostMigration,
Lockstep — all speak Layer 2 under the hood. You don’t call the protocol directly; you use their **drivers**:

```csharp
// server
server.UseRooms(store);  var inv = server.UseInventory(store);  server.UseChat();
// client
var rooms = client.UseRooms();
await rooms.CreateAsync(new RoomOptions { MaxPlayers = 4 });
await rooms.JoinAsync(code);
rooms.PlayerJoined += id => ...;                       // events
rooms.On<Move>(MoveType, (from, m) => Apply(from, m)); // typed room broadcast (client → server → other members)
await rooms.BroadcastAsync(MoveType, new Move { ... });
```

Room broadcast is the quick **peer-to-peer-in-a-room** path (no server validation). For **authoritative** actions
(items, currency, scores), send a command to the server and let it push the result — see the worked example.

### Server-side group membership & broadcast (`IPeerGroups`)

Talk to a whole **group of peers** (a room, a party, …) from the server without hand-maintaining a membership map —
the grouping modules ship these:

```csharp
// Rooms
string? code                   = server.RoomCodeOf(peer);
IReadOnlyList<BasePeer> all    = server.MembersOfRoom(code);
IReadOnlyList<BasePeer> mine   = server.MembersInRoomOf(peer);   // includes peer
IReadOnlyList<BasePeer> others = server.OthersInRoomOf(peer);    // excludes peer
await server.BroadcastToRoomAsync(code, channel, op, msg);                        // the whole room
await server.BroadcastToRoomOfAsync(peer, channel, op, msg, includeSelf: false);  // the peer's room (others)
await server.BroadcastToRoomExceptAsync(code, except, channel, op, msg);          // the room minus one peer

// Party — same shapes
IReadOnlyList<BasePeer> pOthers = server.OthersInPartyOf(peer);
await server.BroadcastToPartyOfAsync(peer, channel, op, msg);
```

These are thin aliases over a reusable primitive, **`IPeerGroups`** — just two methods (`MembersOf(groupKey)` and
`GroupKeyOf(peer)`). To give **your own** module the same query + broadcast family, implement those over its
membership and expose it once:

```csharp
public static IPeerGroups? GuildGroups(this BaseServer server) => /* wrap your guild membership */;

// …and every generic helper works, no extra code:
IReadOnlyList<BasePeer> others = server.GuildGroups().OthersOf(peer);
await server.GuildGroups().BroadcastToGroupOfAsync(peer, channel, op, msg);
await server.GuildGroups().BroadcastExceptAsync(guildKey, except, channel, op, msg);
```

Node-local (members are the live connections on this server), matching how Rooms/Party track membership.

### RPC (separate package `SetNet.Rpc`)

Method-style request/response with its own method-id space (an alternative to a Protocol request channel).

```csharp
RpcRuntime.Enable();                                          // once at startup
TResp r = await client.CallAsync<TReq, TResp>(methodId, req); // client; default 5 s timeout, RpcException on failure

[RpcMethod(methodId)]
public class Handler : IRpcHandler<TReq, TResp>               // server
{
    public Task<TResp> HandleAsync(BasePeer peer, TReq req) => Task.FromResult(new TResp());
}
```

### StateSync (separate package `SetNet.StateSync`) — realtime replication

Server-authoritative entities with delta snapshots (unreliable), interpolation, interest, and an input channel. Use
this for continuous state (positions, health) — **not** the Protocol layer.

```csharp
// both ends register identical archetypes
ReplicaRegistry.Register(ReplicaSchema.Create(1).Field(FieldType.Vector3, interpolate: true).Build());
StateSyncRuntime.Enable();

// server
var world = server.UseStateSync(new StateSyncOptions { TickRate = 30, AutoObserve = false });
var e = world.Spawn(archetype: 1, owner: peerId);      // create replicated entity
e.SetVec3(0, position);                                 // mutate fields whenever; the tick samples them
world.AddObserver(peer);                                // start replicating the world to a peer
world.Despawn(e);
world.InputReceived += (peer, seq, payload) => Apply(peer, payload);

// client
var repl = client.UseStateSync(new StateSyncOptions { TickRate = 30, InterpolationDelayMs = 100 });
repl.EntitySpawned   += view => Instantiate(view);      // create your object/prefab
repl.EntityDespawned += view => Destroy(view);
repl.SendInput(inputBytes);                             // send input for your owned entity
repl.Update();                                          // call each frame to advance interpolation
var me = repl.OwnedEntity; var pos = view.GetVec3(0);
```

Sub-modules: `SetNet.StateSync.{SpatialGrid,LagCompensation,Prediction,NetworkVariable,Rpc}`. `StateSync.Rpc` adds
**entity-scoped RPCs** both directions.

### Streams (separate package `SetNet.Streams`) — large payloads

```csharp
StreamsRuntime.Enable();
await client.UseStreams().SendAsync("map.bin", stream, progress: p => Log(p), streamId: null);   // client → server
await server.UseStreams().SendAsync(peer, "patch.zip", stream);                                    // server → client
// receiver
var streams = client.UseStreams();
streams.OfferReceived += offer => offer.AcceptAsync(new FileStreamSink("out.bin")); // or RejectAsync / auto-accept
```

### Voice (separate package `SetNet.Voice`) — audio relay

```csharp
VoiceRuntime.Enable();  server.UseVoice();
var voice = client.UseVoice();
voice.JoinChannel(1);
voice.SendFrame(1, opusBytes);                                  // unreliable, opaque audio
voice.FrameReceived += (speakerId, ch, audio) => Play(audio);
```

### Relay (separate package `SetNet.Relay`) — opaque forwarding

```csharp
RelayRuntime.Enable();  server.UseRelay();
var relay = client.UseRelay();
var code = await relay.AllocateAsync(maxPeers: 8);   // host
await relay.JoinAsync(code);                          // guest
await relay.SendAsync(bytes);                         // fan-out to session peers
relay.Received += (fromPeerId, bytes) => ...;
```

### Cluster (separate package `SetNet.Cluster`) — server ↔ server

```csharp
var node = new ClusterNode(new ClusterNodeOptions { NodeId = "a", ListenPort = 7000, Seeds = new[]{ "b:7000" } });
node.On<NodeEvent>(e => ...);
node.Publish(new NodeEvent { ... });                 // best-effort broadcast to the mesh
```

### Transparent layers (no send API)

`SetNet.Fragmentation` (oversize UDP), `SetNet.Multiplex` (per-channel ordering), `SetNet.Congestion`,
`SetNet.Priority`, `SetNet.Compression` — enable them and they shape traffic under your existing sends.

---

## Which method should I use?

| I need to… | Use |
|---|---|
| One-off message, my own type, maybe unreliable | Core `SendAsync<T>` + `[MessageHandler]` |
| Ask the server and await an answer | `client.RequestAsync<TReq,TResp>` + server `[Op]`/`IChannelService` (or `SetNet.Rpc`) |
| Tell the server something, no answer | `client.PostAsync` + server `[Op]` |
| Server notifies clients (events) | `peer.PublishAsync` / `peers.PublishAsync` + client `On<T>` / `[Event]` |
| Relay a message between clients in a room (non-authoritative) | `rooms.BroadcastAsync` + `rooms.On<T>` |
| Authoritative item/currency/score change visible to others | command to server → validate (Inventory/Wallet) → `PublishAsync` to members (see example) |
| Continuous state (positions, health), many entities | `SetNet.StateSync` |
| Big file / blob | `SetNet.Streams` |
| Voice | `SetNet.Voice` |
| Server-to-server | `SetNet.Cluster` |

---

## Direction reference

| From → To | Mechanism |
|---|---|
| Client → Server (no reply) | core `SendAsync` · `PostAsync` · `rooms.BroadcastAsync` · `voice.SendFrame` · StateSync `SendInput` |
| Client → Server → Client (reply) | `RequestAsync` · RPC `CallAsync` |
| Server → one Client | `peer.SendAsync` · `peer.PublishAsync` · StateSync (per-observer) |
| Server → many Clients | `peers.PublishAsync` · room member fan-out · StateSync tick · Voice fan-out |
| Client subscribe to pushes | `On<T>`/`OnRaw`/`[Event]` · module driver events (`rooms.PlayerJoined`, `inv.Changed`, …) |
| Server ↔ Server | `SetNet.Cluster` |

---

## Worked example — “drop an item, the other 3 players see it”

**Scenario.** Dedicated server, **unlimited rooms**, 4 players per room. A player drops an item from their inventory;
the item must leave their inventory (authoritative) and appear to the **other 3 players in the same room** only.

**Why not a plain room broadcast?** A broadcast is client-to-client and unvalidated — a cheating client could
“drop” items it doesn’t own. So the drop is a **command to the server**, which:
1. revokes the item from the sender’s inventory (`InventoryServer.TryRevokeAsync` — atomic, authoritative);
2. finds the sender’s room and pushes an `ItemDropped` **event to the other members**.

This combines **Rooms** (grouping), **Inventory** (authority), and a **custom Protocol channel** (the action + event).

### Shared contracts (referenced by both ends)

```csharp
using MessagePack;
using SetNet.Protocol;

public static class GameChannels { public const ushort World = 1000; } // app channel (avoid Channels 1–24)
public enum WorldOp  : ushort { Drop = 1 }
public enum WorldEvt : ushort { ItemDropped = 10 }

[MessagePackObject] public class DropReq     { [Key(0)] public string ItemId {get;set;}=""; [Key(1)] public long Count {get;set;} [Key(2)] public float X {get;set;} [Key(3)] public float Y {get;set;} }
[MessagePackObject] public class ItemDropped { [Key(0)] public string PlayerId {get;set;}=""; [Key(1)] public string ItemId {get;set;}=""; [Key(2)] public long Count {get;set;} [Key(3)] public float X {get;set;} [Key(4)] public float Y {get;set;} }
```

### Server

```csharp
using SetNet.Core;
using SetNet.Inventory;
using SetNet.Protocol;
using SetNet.Rooms;

public sealed class GamePeer : BasePeer
{
    public GamePeer(PeerInfo info) : base(info) { }
    protected override void OnDisconnected() { }
    protected override void OnError(string error) { }
}

public sealed class GameServer : BaseServer
{
    public InventoryServer Inventory { get; }

    public GameServer(Configuration cfg) : base(cfg)
    {
        this.UseRooms(new MemoryRoomStore());               // unlimited rooms; the module tracks membership for us
        Inventory = this.UseInventory(new MemoryInventoryStore(),
            new InventoryOptions { /* PlayerKey = peer => accountIdOf(peer) with SetNet.Auth for durable items */ });
    }

    protected override BasePeer OnNewClient(PeerInfo info)
    {
        var peer = new GamePeer(info);
        peer.StartReceive();
        return peer;
    }
}

// The drop action lives on its own channel — one [Op] method, resolved to the concrete server via the peer.
[ProtocolChannel(GameChannels.World)]
public sealed class WorldService
{
    [Op((ushort)WorldOp.Drop)]
    public async Task Drop(BasePeer peer, DropReq req)          // fire-and-forget from the client
    {
        var srv = (GameServer)peer.CurrentPeerInfo.Server!;
        var key = srv.Inventory.KeyOf(peer);

        // Authoritative: only proceed if the player actually has the item (also pushes their inventory 'Changed').
        if (!await srv.Inventory.TryRevokeAsync(key, req.ItemId, req.Count)) return;

        // Built-in Rooms helper — push to the OTHER members of the dropper's room (no hand-maintained membership map).
        await srv.BroadcastToRoomOfAsync(peer, GameChannels.World, (ushort)WorldEvt.ItemDropped,
            new ItemDropped { PlayerId = key, ItemId = req.ItemId, Count = req.Count, X = req.X, Y = req.Y });
    }
}
```

### Client

```csharp
using SetNet.Inventory;
using SetNet.Protocol;
using SetNet.Rooms;

// startup (once)
SetNetSerializer.Use(new MessagePackNetSerializer());
RoomsRuntime.Enable(); InventoryRuntime.Enable();   // load module assemblies for discovery

var client = new GameClient(config);
var rooms  = client.UseRooms();
var inv    = client.UseInventory();

inv.Changed += stacks => RefreshInventoryUi(stacks);                 // dropper sees their own inventory shrink
client.On<ItemDropped>(GameChannels.World, (ushort)WorldEvt.ItemDropped,
    e => SpawnWorldPickup(e.ItemId, e.Count, e.X, e.Y));            // the other 3 see the drop

await client.ConnectAsync();
await rooms.JoinAsync(roomCode);                                     // or via SetNet.Matchmaking

// drop an item:
await client.PostAsync(GameChannels.World, (ushort)WorldOp.Drop,
    new DropReq { ItemId = "sword", Count = 1, X = playerX, Y = playerY });
```

### How it scales & the fine print

- **Unlimited rooms.** The Rooms module tracks membership per room, so `server.BroadcastToRoomOfAsync(peer, …)` /
  `server.OthersInRoomOf(peer)` fan out to exactly the sender’s room — no per-app membership map. Players join by
  code or via `SetNet.Matchmaking` (pass the **same** `IRoomStore` to `UseRooms` and `UseMatchmaking`).
- **The event only reaches that room.** `BroadcastToRoomOfAsync` pushes to the 3 other members’ own connections, so
  no other room sees it. The dropper is excluded by default (they already know; their `inv.Changed` fires).
- **Authoritative & dupe-safe.** `TryRevokeAsync` is atomic; if the player doesn’t hold the item, nothing happens.
- **Durable inventories.** On a real dedicated server set `InventoryOptions.PlayerKey` to the authenticated account
  id (via `SetNet.Auth`) and use a Redis/DB `IInventoryStore`, so items survive reconnects and follow the player.
- **Picking it up** is the mirror op: a `Pickup` `[Op]` that `GrantAsync`es to the picker and pushes an
  `ItemPickedUp` event so others remove the world object.
- **Add live movement** with `SetNet.StateSync` on the same connection (positions over the unreliable channel), while
  drops/pickups stay on this reliable Protocol channel — exactly the “use everything together” point.
- **Co-located clients caveat.** `On<T>` subscriptions are process-wide; with one client per process (normal for real
  players) this is exactly right. If you run several clients in one process (bots/tests), include the room code in the
  event and filter inside the handler.

### Extending — your own channel, end-to-end

The example above *is* the recipe for a custom channel: pick a `ushort` channel id, define `op`/`event` enums, add a
`[ProtocolChannel] [Op]` service on the server, and call `PostAsync`/`RequestAsync` + subscribe with `On<T>`/`[Event]`
on the client. Nothing else to register — the two core envelope handlers are always present.

---

See also: **[MODULES.md](MODULES.md)** (full package catalog) · **[GUIDE.en.md](GUIDE.en.md)** (getting started) ·
**[README.md](README.md)** (docs index).
