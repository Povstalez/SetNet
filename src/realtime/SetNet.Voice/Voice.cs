using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Voice
{
    /// <summary>Reserved wire types for the voice relay. Don't reuse these ids for application messages.</summary>
    public static class VoiceTypes
    {
        /// <summary>Server → client: a relayed voice frame from another speaker.</summary>
        public const ushort VoiceDown = ushort.MaxValue - 32;   // 65503

        /// <summary>Client → server: an outgoing voice frame for a channel.</summary>
        public const ushort VoiceUp = ushort.MaxValue - 31;     // 65504

        /// <summary>Client → server: join/leave a voice channel.</summary>
        public const ushort VoiceControl = ushort.MaxValue - 30; // 65505
    }

    internal static class VoiceOps { public const byte Join = 0; public const byte Leave = 1; }

    /// <summary>
    /// Server-side voice relay hub. It never encodes or decodes audio — voice frames are opaque bytes (Opus, raw PCM,
    /// whatever your client produces). Clients join numeric channels; each <c>VoiceUp</c> frame is fanned out to every
    /// other member of that channel as a <c>VoiceDown</c> frame tagged with a stable speaker id. Voice is sent
    /// unreliably (loss-tolerant, low latency). Enable with <see cref="VoiceServerExtensions.UseVoice"/>.
    /// </summary>
    public static class VoiceServer
    {
        // Per-server voice state: speaker-id assignment + channel membership.
        private sealed class State
        {
            public readonly ConditionalWeakTable<BasePeer, object> Ids = new ConditionalWeakTable<BasePeer, object>();
            public readonly ConcurrentDictionary<ushort, HashSet<BasePeer>> Channels = new ConcurrentDictionary<ushort, HashSet<BasePeer>>();
            private int _next;
            public uint IdOf(BasePeer peer) => (uint)((int)Ids.GetValue(peer, _ => System.Threading.Interlocked.Increment(ref _next)));
        }

        private static readonly ConditionalWeakTable<BaseServer, State> Servers = new ConditionalWeakTable<BaseServer, State>();

        /// <summary>Ensures voice state is attached to a server (idempotent).</summary>
        internal static void Ensure(BaseServer server) => StateOf(server);

        private static State StateOf(BaseServer server) => Servers.GetValue(server, s => Attach(s));

        private static State Attach(BaseServer server)
        {
            var state = new State();
            server.PeerDisconnected += peer =>
            {
                foreach (var kv in state.Channels)
                    lock (kv.Value) kv.Value.Remove(peer);
            };
            return state;
        }

        internal static void OnControl(BasePeer peer, byte op, ushort channel)
        {
            var server = peer.CurrentPeerInfo.Server;
            if (server == null) return;
            var members = StateOf(server).Channels.GetOrAdd(channel, _ => new HashSet<BasePeer>());
            lock (members) { if (op == VoiceOps.Join) members.Add(peer); else members.Remove(peer); }
        }

        internal static void OnVoice(BasePeer peer, ushort channel, byte[] voice, int offset)
        {
            var server = peer.CurrentPeerInfo.Server;
            if (server == null) return;
            var state = StateOf(server);
            if (!state.Channels.TryGetValue(channel, out var members)) return;

            var senderId = state.IdOf(peer);
            var down = new byte[6 + (voice.Length - offset)];
            BinaryPrimitives.WriteUInt32LittleEndian(down.AsSpan(0, 4), senderId);
            BinaryPrimitives.WriteUInt16LittleEndian(down.AsSpan(4, 2), channel);
            Buffer.BlockCopy(voice, offset, down, 6, voice.Length - offset);

            BasePeer[] targets;
            lock (members) targets = System.Linq.Enumerable.ToArray(members);
            foreach (var target in targets)
            {
                if (ReferenceEquals(target, peer)) continue;
                _ = TrySend(target, down);
            }
        }

        private static async Task TrySend(BasePeer peer, byte[] frame)
        {
            try { await peer.SendAsync(VoiceTypes.VoiceDown, frame, DeliveryMethod.Unreliable).ConfigureAwait(false); } catch { /* dropped */ }
        }
    }

    /// <summary>Client-side voice channel: join/leave channels, push frames, and receive others' frames.</summary>
    public sealed class VoiceChannel
    {
        private static readonly ConcurrentDictionary<BaseClient, VoiceChannel> Clients = new ConcurrentDictionary<BaseClient, VoiceChannel>();
        private readonly BaseClient _client;

        /// <summary>Raised for every incoming voice frame: (speakerId, channel, opaque audio bytes).</summary>
        public event Action<uint, ushort, byte[]>? FrameReceived;

        internal VoiceChannel(BaseClient client) { _client = client; Clients[client] = this; }

        /// <summary>Joins a voice channel so this client receives (and can send to) its traffic.</summary>
        public Task JoinChannel(ushort channel) => Control(VoiceOps.Join, channel);

        /// <summary>Leaves a voice channel.</summary>
        public Task LeaveChannel(ushort channel) => Control(VoiceOps.Leave, channel);

        /// <summary>Sends one opaque voice frame (e.g. an encoded Opus packet) to a channel. Unreliable.</summary>
        public Task SendFrame(ushort channel, byte[] audio)
        {
            var frame = new byte[2 + audio.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), channel);
            Buffer.BlockCopy(audio, 0, frame, 2, audio.Length);
            return _client.SendAsync(VoiceTypes.VoiceUp, frame, DeliveryMethod.Unreliable);
        }

        private Task Control(byte op, ushort channel)
        {
            var frame = new byte[3];
            frame[0] = op;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), channel);
            return _client.SendAsync(VoiceTypes.VoiceControl, frame, DeliveryMethod.Reliable);
        }

        internal static void Dispatch(uint speakerId, ushort channel, byte[] audio)
        {
            foreach (var vc in Clients.Values) vc.FrameReceived?.Invoke(speakerId, channel, audio);
        }
    }

    /// <summary>Attaches the voice relay to a server by composition.</summary>
    public static class VoiceServerExtensions
    {
        /// <summary>Enables the server-side voice relay hub.</summary>
        public static void UseVoice(this BaseServer server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            VoiceServer.Ensure(server);
        }
    }

    /// <summary>Attaches a voice channel to a client by composition.</summary>
    public static class VoiceClientExtensions
    {
        /// <summary>Enables client-side voice; returns the channel handle to join/send/receive.</summary>
        public static VoiceChannel UseVoice(this BaseClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return new VoiceChannel(client);
        }
    }

    /// <summary>Auto-discovered server handler for outgoing voice frames.</summary>
    [MessageHandler(VoiceTypes.VoiceUp)]
    public sealed class VoiceUpHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            if (data.Length >= 2) VoiceServer.OnVoice(peer, BinaryPrimitives.ReadUInt16LittleEndian(data), data, 2);
            return Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered server handler for channel join/leave.</summary>
    [MessageHandler(VoiceTypes.VoiceControl)]
    public sealed class VoiceControlHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            if (data.Length >= 3) VoiceServer.OnControl(peer, data[0], BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1, 2)));
            return Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for relayed voice frames.</summary>
    [MessageHandler(VoiceTypes.VoiceDown)]
    public sealed class VoiceDownHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            if (data.Length >= 6)
            {
                var speaker = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
                var channel = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2));
                var audio = new byte[data.Length - 6];
                Buffer.BlockCopy(data, 6, audio, 0, audio.Length);
                VoiceChannel.Dispatch(speaker, channel, audio);
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>One-time bootstrap so the voice handlers are discovered. Call at startup.</summary>
    public static class VoiceRuntime
    {
        /// <summary>Ensures the voice layer is discoverable.</summary>
        public static void Enable() { _ = VoiceTypes.VoiceUp; }
    }
}
