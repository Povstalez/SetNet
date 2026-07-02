<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.StateSync.Rpc

**Entity-scoped RPCs for [SetNet.StateSync](https://www.nuget.org/packages/SetNet.StateSync).**

Regular [`SetNet.StateSync`](https://www.nuget.org/packages/SetNet.StateSync) replicates *continuous* state (positions, health) every tick. But some things are **one-off events tied to a specific entity** — "this door opened", "that unit fired", "play the hit VFX on player 7". Streaming those as replicated fields is wasteful and lossy. This package adds a small **targeted RPC channel keyed by `NetId`**:

- **server → client**: call a method on one client's copy of an entity (its owner, or any observer).
- **client → server**: the owner of an entity invokes a method on the server (which validates and applies it).

It's a thin, reliable message channel — you map a `methodId` to your own C# method on the matching object. For fully-typed request/response (with a return value) use [`SetNet.Rpc`](https://www.nuget.org/packages/SetNet.Rpc) instead; this one is fire-and-forget and entity-addressed.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.StateSync
dotnet add package SetNet.StateSync.Rpc
```

## Setup

```csharp
StateSyncRuntime.Enable();
StateSyncRpcRuntime.Enable();     // once at startup, both ends, before creating client/server
```

## Server

Register a **typed handler per method id** with `On<T>` — the payload is deserialized for you, so you work with your DTO, not bytes:

```csharp
var rpc = server.UseStateSyncRpc();

// one typed handler per method — validate ownership/authority inside it:
rpc.On<FireCommand>((ushort)UnitRpc.Fire, (peer, netId, shot) =>
{
    if (Owns(peer, netId)) ApplyFire(netId, shot);
});
rpc.On<UseItem>((ushort)UnitRpc.UseItem, (peer, netId, cmd) => { /* … */ });

// push a call to a specific client (typed — serialized for you):
await rpc.SendAsync(ownerPeer, netId, (ushort)UnitRpc.Fire, new FireCommand { Dir = dir });

// …or to several observers (you decide who):
foreach (var observer in observersOf(netId))
    await rpc.SendAsync(observer, netId, (ushort)UnitRpc.PlayHitFx, hitInfo);
```

## Client

```csharp
var rpc = client.UseStateSyncRpc();

rpc.On<HitInfo>((ushort)UnitRpc.PlayHitFx, (netId, hit) => FindObject(netId)?.PlayHit(hit));
rpc.On<DoorState>((ushort)UnitRpc.DoorChanged, (netId, s) => FindObject(netId)?.SetDoor(s));

// invoke on the entity you own (server validates):
await rpc.SendAsync(myNetId, (ushort)UnitRpc.Fire, new FireCommand { Dir = aimDir });
```

## Typed handlers vs the raw catch-all

The channel routes each incoming call like this:

1. If a `On<T>(methodId, …)` handler is registered for the method → the payload is **deserialized to `T`** and that handler runs. This is the recommended path — one typed handler per method id, no `byte[]`.
2. Otherwise the raw **`Received`** event fires with `(…, methodId, byte[] payload)` — a catch-all for methods you didn't register (relays, logging, dynamic dispatch). Decode it yourself with `SetNetSerializer.Deserialize<T>(payload)`.

Why the split? A single C# event can't be generic over your type when **each `methodId` carries a different argument type** — so the typed layer is a *registration* (`On<T>`), and the untyped event is the fallback. `Off(methodId)` removes a typed handler.

## API

| Member | Side | Purpose |
|---|---|---|
| `server.UseStateSyncRpc()` → `ServerStateRpc` | server | enable + get the channel |
| `ServerStateRpc.On<T>(methodId, Action<BasePeer,uint,T>)` | server | **typed** handler for a method (deserializes the arg) |
| `ServerStateRpc.SendAsync<T>(peer, netId, methodId, arg, delivery?)` | server | call a method on one client's entity |
| `ServerStateRpc.Received` / `Off(methodId)` | server | raw catch-all for unregistered methods / unregister |
| `client.UseStateSyncRpc()` → `ClientStateRpc` | client | enable + get the channel |
| `ClientStateRpc.On<T>(methodId, Action<uint,T>)` | client | **typed** handler for a method |
| `ClientStateRpc.SendAsync<T>(netId, methodId, arg, delivery?)` | client | call a method on the server (owned entity) |
| `ClientStateRpc.Received` / `Off(methodId)` | client | raw catch-all / unregister |

Reliable by default; pass `DeliveryMethod.Unreliable` for frequent, loss-tolerant effects. Reserved wire type `65516`.

## Notes

- **One handler per method id.** `On<T>` overwrites any previous handler for the same id; use distinct method ids per call type. The Unity layer can bind these to methods on a `NetworkObject`.
- **Authority**: the server must validate that a client-invoked RPC is legal for the peer that sent it (e.g. the peer owns `netId`). The server is authoritative — never trust the client.
- Routing to the right client uses a process-wide registry (one client per process is the common case; co-located clients share routing).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
