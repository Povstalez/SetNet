<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Notifications

**Server→client notifications / toasts for [SetNet](https://www.nuget.org/packages/SetNet) — pushed if the player is online, queued if not.**

Fire a `Notification` at one player or broadcast to everyone. If the target is online it arrives instantly; if they're offline it's stored in a pluggable `INotificationStore` and flushed the moment they reconnect, so achievements, mail alerts and system messages never fall on the floor. Clients just subscribe to `Received`. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Notifications
```

## Usage

```csharp
// server
var notes = server.UseNotifications();
await notes.NotifyAsync(playerKey, new Notification("achievement", "First Blood", "You won your first match!"));
await notes.BroadcastAsync(new Notification("info", "Maintenance", "Server restarts in 5 minutes."));

// client
NotificationRuntime.Enable();
var notes = client.UseNotifications();
notes.Received += n => Toast(n.Kind, n.Title, n.Body);   // n.Data is your opaque payload
```

## API

**Server:** `server.UseNotifications(INotificationStore?, NotificationOptions?)` → `NotificationServer` — `NotifyAsync(playerKey, Notification)` (push-or-queue), `BroadcastAsync(Notification)` (online only).
**Client:** `client.UseNotifications()` → `NotificationClient` — `event Received`.
**Model:** `Notification` (`Kind`, `Title`, `Body`, `byte[]? Data`).
**Store:** `INotificationStore` (`MemoryNotificationStore` default) — `EnqueueAsync`/`DrainAsync`.
**Options:** `NotificationOptions.PlayerKey`. `NotificationRuntime.Enable()` — one-time bootstrap.

## Notes

- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Notifications` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. It's a push-only channel, so `Notification` bodies are hand-framed `byte[]` and there's nothing to discover server-side.
- **Online = instant, offline = queued.** `NotifyAsync` pushes to a connected peer; if there's no live peer (or the push fails) it falls back to `INotificationStore.EnqueueAsync`, and the queue is drained on the next `PeerConnected`. `BroadcastAsync` reaches only currently-online players — it's not queued.
- **Stable player key.** Default is the connection id; with [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth) set `NotificationOptions.PlayerKey` to the account id so queued notifications survive a reconnect from a new connection.
- **Durable across restarts / nodes.** The default `MemoryNotificationStore` is in-process; swap in a Redis/DB store (see [`SetNet.Persistence`](https://www.nuget.org/packages/SetNet.Persistence)) so offline queues survive restarts and span a cluster.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
