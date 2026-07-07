# SetNet.WebSockets.Unity

A **WebSocket transport for SetNet inside Unity — including WebGL (browser)**.

Unity's own `System.Net.WebSockets.ClientWebSocket` works on Standalone/Mobile/Editor but **not in WebGL** (no threads, no sockets). This package fills that gap: on WebGL it talks to the browser's WebSocket through a tiny `.jslib` bridge; on every other platform it uses `ClientWebSocket`. Everything above the transport (message handlers, RPC, rooms, auth, StateSync…) is unchanged.

**Client-only.** Host the server on a dedicated .NET machine with the [`SetNet.WebSockets`](https://www.nuget.org/packages/SetNet.WebSockets) package — the framing is identical, so one server serves WebGL and native clients.

## Install (UPM)

Package Manager → *Add package from git URL* (or a local path):

```
https://github.com/Povstalez/SetNet.git?path=/src/engine/SetNet.WebSockets.Unity
```

Also import the SetNet core assemblies (`SetNet.dll`, plus a serializer like `SetNet.MessagePack.dll`) into `Assets/Plugins/`.

## Use

```csharp
using SetNet.Config;
using SetNet.WebSockets.Unity;

var config = new Configuration { Host = "game.example.com", Port = 443 }
    .UseUnityWebSockets(secure: true);     // wss:// (required on https pages / Telegram Mini Apps); use false for ws://

var client = new MyGameClient(config);     // your BaseClient subclass — nothing else changes
await client.ConnectAsync();
await client.SendAsync((ushort)Msg.Hello, payload);
```

That's it — the same `BaseClient` code runs on Editor, Windows/Mac/Linux, Android/iOS, **and WebGL**.

## WebGL notes (important)

- **Single-threaded.** WebGL has no threads, so the transport polls the browser between frames — **drive your client from the main thread** and don't block it. Incoming messages arrive as the Unity player loop runs.
- **Main-thread callbacks.** Route your message-handler work onto the main thread with [`SetNet.Unity`](https://www.nuget.org/packages/SetNet.Unity)'s `MainThreadDispatcher` (`Post` in a handler, `Drain()` in `Update()`), exactly as for other transports.
- **`wss://` on https.** A WebGL build served over https (itch.io, GitHub Pages, Telegram) can only open `wss://`. Use `UseUnityWebSockets(secure: true)` and terminate TLS at a reverse proxy in front of your `SetNet.WebSockets` server.
- The `.jslib` lives in `Plugins/WebGL/` and is picked up automatically by WebGL builds.

## How it works

One binary WebSocket message == one SetNet frame: `[2-byte type LE][payload]` (message boundaries replace TCP's length prefix). Reliable + ordered, so `DeliveryMethod`/channel are ignored. The WebGL bridge (`SetNetWebSocket.jslib`) queues incoming `ArrayBuffer` messages; the C# side (`WebGLWebSocketConnection`) polls that queue, yielding to the player loop until a message is ready.

## License

MIT
