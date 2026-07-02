<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Voice

**Codec-agnostic voice-chat relay for [SetNet](https://www.nuget.org/packages/SetNet).**

A server-side relay for real-time voice. Clients join numeric **channels** and push **opaque audio frames** — the server never encodes or decodes anything; a frame is just bytes (an encoded Opus packet, raw PCM, Speex, whatever your capture produces). Each frame is fanned out to the channel's other members over the **unreliable** channel (loss-tolerant, low-latency, the right trade-off for voice). Every relayed frame carries a stable **speaker id** so the receiver can mix per-speaker. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Voice
```

## Usage

Call `VoiceRuntime.Enable()` once at startup on both ends (before handler discovery).

**Server** — just turn the relay on:

```csharp
VoiceRuntime.Enable();
server.UseVoice();
```

**Client** — join a channel, push captured audio, play back what you receive:

```csharp
VoiceRuntime.Enable();

var voice = client.UseVoice();
await voice.JoinChannel(1);

voice.FrameReceived += (speakerId, channel, audio) =>
{
    // decode `audio` (e.g. Opus → PCM) and play it, keyed by speakerId
    playback.Feed(speakerId, decoder.Decode(audio));
};

// from your microphone capture loop (~20 ms frames):
await voice.SendFrame(1, encoder.Encode(pcmFrame));
```

> This package handles **transport only**. Capture, encode/decode (Opus etc.), jitter buffering, and mixing are yours — SetNet just moves the bytes.

## API

**Server:** `server.UseVoice()` — enable the relay hub.

**Client:** `var voice = client.UseVoice()` → `VoiceChannel`

| Member | Purpose |
|---|---|
| `Task JoinChannel(ushort channel)` | start receiving/sending on a channel |
| `Task LeaveChannel(ushort channel)` | stop |
| `Task SendFrame(ushort channel, byte[] audio)` | relay one opaque frame to the channel (unreliable) |
| `event Action<uint, ushort, byte[]> FrameReceived` | `(speakerId, channel, audio)` for each incoming frame |

`VoiceRuntime.Enable()` — one-time bootstrap so the voice handlers are discovered.

## Notes

- **Reserved wire types 65503 / 65504 / 65505.** Don't reuse them.
- **Unreliable by design.** Voice frames use `DeliveryMethod.Unreliable`; a UDP or Both transport gives the best latency. Over a reliable-only transport (plain TCP/WebSocket) they still deliver, but head-of-line blocking can add jitter.
- **Bring your own MTU discipline.** Keep encoded frames comfortably under the transport's datagram limit (SetNet's UDP default rejects payloads over ~1200 bytes); 20 ms Opus frames are well within this.
- **No server-side mixing.** The server forwards per-speaker frames; clients mix. This keeps the server cheap and codec-agnostic. Pair with [`SetNet.Rooms`](https://www.nuget.org/packages/SetNet.Rooms) or [`SetNet.Party`](https://www.nuget.org/packages/SetNet.Party) and map their ids to voice channels for lobby/party voice.
- **No built-in auth.** Anyone who can reach the server can join a channel — gate it with [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth) if channels are private.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
