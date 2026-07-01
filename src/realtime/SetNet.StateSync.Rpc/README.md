# SetNet.StateSync.Rpc

**Entity-scoped RPCs for [SetNet.StateSync](https://www.nuget.org/packages/SetNet.StateSync).**

Send a method call tagged with a `NetId` to a specific client (server → client) or to the server (client → owned entity). A thin, targeted channel — you dispatch `methodId` to your own method on the matching object.

```csharp
StateSyncRpcRuntime.Enable();   // startup, both ends

// server:
var rpc = server.UseStateSyncRpc();
rpc.Received += (peer, netId, methodId, payload) => { /* validate + apply */ };
await rpc.SendAsync(ownerPeer, netId, methodId: 1, payload);      // server → a client

// client:
var rpc = client.UseStateSyncRpc();
rpc.Received += (netId, methodId, payload) => { /* play effect on that object */ };
await rpc.SendAsync(myNetId, methodId: 2, payload);              // client → server (owned entity)
```

Reliable by default. Method binding (which C# method a `methodId` maps to) is left to your code / the Unity layer.

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
