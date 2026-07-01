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

```csharp
var rpc = server.UseStateSyncRpc();

// receive calls from clients (validate ownership/authority yourself):
rpc.Received += (peer, netId, methodId, payload) =>
{
    switch (methodId)
    {
        case (ushort)UnitRpc.Fire:
            var shot = SetNetSerializer.Deserialize<FireCommand>(payload);
            if (Owns(peer, netId)) ApplyFire(netId, shot);
            break;
    }
};

// push a call to a specific client (e.g. the entity's owner):
await rpc.SendAsync(ownerPeer, netId, (ushort)UnitRpc.Fire, new FireCommand { Dir = dir });

// …or broadcast to several observers (you decide who):
foreach (var observer in observersOf(netId))
    await rpc.SendAsync(observer, netId, (ushort)UnitRpc.PlayHitFx, hitInfo);
```

## Client

```csharp
var rpc = client.UseStateSyncRpc();

rpc.Received += (netId, methodId, payload) =>
{
    if (methodId == (ushort)UnitRpc.PlayHitFx)
    {
        var hit = SetNetSerializer.Deserialize<HitInfo>(payload);
        FindObject(netId)?.PlayHit(hit);
    }
};

// invoke on the entity you own (server validates):
await rpc.SendAsync(myNetId, (ushort)UnitRpc.Fire, new FireCommand { Dir = aimDir });
```

## Typed vs raw payloads

- `SendAsync<T>(…, T arg)` **serializes** the argument via `SetNetSerializer` — pass your DTO directly.
- On the receive side you get `byte[]`, because different `methodId`s carry **different argument types** (a single event can't be generic). Deserialize per `methodId` with `SetNetSerializer.Deserialize<T>(payload)`.
- The raw `SendAsync(…, byte[] payload)` overload is there if you already have serialized bytes.

## API

| Member | Side | Purpose |
|---|---|---|
| `server.UseStateSyncRpc()` → `ServerStateRpc` | server | enable + get the channel |
| `ServerStateRpc.SendAsync<T>(peer, netId, methodId, arg, delivery?)` | server | call a method on one client's entity |
| `ServerStateRpc.Received` | server | `(peer, netId, methodId, payload)` from a client |
| `client.UseStateSyncRpc()` → `ClientStateRpc` | client | enable + get the channel |
| `ClientStateRpc.SendAsync<T>(netId, methodId, arg, delivery?)` | client | call a method on the server (owned entity) |
| `ClientStateRpc.Received` | client | `(netId, methodId, payload)` from the server |

Reliable by default; pass `DeliveryMethod.Unreliable` for frequent, loss-tolerant effects. Reserved wire type `65516`.

## Notes

- **Method binding** (which C# method a `methodId` maps to) is your code — typically a `switch` or a dictionary. The Unity layer can bind these to methods on a `NetworkObject`.
- **Authority**: the server must validate that a client-invoked RPC is legal for the peer that sent it (e.g. the peer owns `netId`). The server is authoritative — never trust the client.
- Routing to the right client uses a process-wide registry (one client per process is the common case; co-located clients share routing).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
