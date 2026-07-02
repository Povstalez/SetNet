<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.ProofOfWork

**Hashcash admission gate for [SetNet](https://www.nuget.org/packages/SetNet) — make mass/bot connections expensive.**

On connect the server issues a random challenge; until the client sends a nonce whose `SHA-256(challenge ‖ nonce)` has enough **leading zero bits**, the server **drops all of that peer's application frames**. A single honest client burns a fraction of a second of CPU once; an attacker opening thousands of connections must pay that cost thousands of times. The client side solves and submits automatically. Added by **composition** — the server gate is *chained* onto any existing `InboundAuthorizer`.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.ProofOfWork
```

## Usage

Call `ProofOfWorkRuntime.Enable()` once at startup (before handler discovery) on **both** ends so the handshake handlers are registered.

**Server:**

```csharp
ProofOfWorkRuntime.Enable();

var server = new MyServer(config);
server.UseProofOfWork(difficulty: 20);   // required leading zero bits (~2^20 hashes)
await server.StartAsync();
```

**Client:**

```csharp
ProofOfWorkRuntime.Enable();

var client = new MyClient(config);
client.UseProofOfWork();                  // auto-solves the challenge on connect
await client.ConnectAsync();
```

That's it — the client solves the challenge off the receive thread and submits the nonce; once verified, its frames flow normally.

## API

| Call | Purpose |
|---|---|
| `server.UseProofOfWork(int difficulty = 20)` | install the gate; peers must solve before their frames pass |
| `client.UseProofOfWork()` | auto-solve and submit on connect / reconnect |
| `ProofOfWorkRuntime.Enable()` | one-time bootstrap so the PoW handlers are discovered |

**Difficulty** is the number of required leading zero bits. Cost is roughly `2^difficulty` SHA-256 hashes:

| difficulty | ~hashes | typical solve time |
|---|---|---|
| 16 | 65 K | instant |
| 20 | 1 M | tens of ms |
| 22 | 4 M | ~0.1–0.3 s |
| 24 | 16 M | ~0.5–1 s |

Pick the highest value your slowest legitimate client tolerates.

## Notes

- **Reserved wire types 65506 / 65507.** Don't reuse them for application messages.
- **Standalone admission gate.** PoW blocks *all* non-PoW frames until solved. If you also use another "deny-until" gate that blocks its own control frames (e.g. [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth)), run PoW as the connection-admission step and let auth happen after — stacking two mutually-blocking gates can deadlock. [`SetNet.RateLimit`](https://www.nuget.org/packages/SetNet.RateLimit) and [`SetNet.BanList`](https://www.nuget.org/packages/SetNet.BanList) chain fine.
- **Mitigation, not authentication.** PoW raises the cost of connection floods; it does not identify users. Combine with auth for identity and with rate limiting for sustained abuse.
- **One client per process** is the common case and fully supported; the challenge is solved and answered on the connection that received it.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
