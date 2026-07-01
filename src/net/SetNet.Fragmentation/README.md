<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Fragmentation

**Application-level UDP fragmentation for [SetNet](https://www.nuget.org/packages/SetNet).**

SetNet caps a UDP datagram at `UdpMaxDatagramPayload` (default 1200 B) and rejects anything larger. This package splits an oversize message into numbered fragments and **reassembles them transparently** on the far side — the whole message is delivered to its normal typed handler.

Only needed on **UDP** (TCP/WebSockets are streams and carry any size already). Most useful with **reliable** delivery, since over unreliable UDP a single lost fragment loses the whole message.

## Install & use

```bash
dotnet add package SetNet
dotnet add package SetNet.Fragmentation
```

```csharp
FragmentationRuntime.Enable();     // at startup (both ends), before creating client/server
client.UseFragmentation();         // client reassembles incoming fragmented messages

// send (splits only if it exceeds maxChunk):
await client.SendFragmentedAsync((ushort)MsgType.BigState, bytes, DeliveryMethod.Reliable);
await peer.SendFragmentedAsync((ushort)MsgType.BigState, bytes, DeliveryMethod.Reliable);
```

- Fragment frame: `[4 msgId][2 origType][2 index][2 count][chunk]`, reserved wire type `65517`.
- Reassembly is bounded (in-flight cap + staleness timeout) so lost/never-completed sets can't leak memory.
- `SendFragmentedAsync` sends the message whole when it already fits, so it's safe to use everywhere.

## License

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
