using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.Chat
{
    /// <summary>Command operations (client → server) within the Chat protocol channel (fire-and-forget).</summary>
    internal enum ChatOp : ushort { Join = 1, Leave = 2, Message = 3 }

    /// <summary>Push events (server → client) within the Chat protocol channel.</summary>
    internal enum ChatEvt : ushort { Message = 10 }

    /// <summary>Options for the chat server.</summary>
    public sealed class ChatOptions
    {
        /// <summary>Words to censor (whole-word, case-insensitive), replaced with asterisks. Optional.</summary>
        public IReadOnlyCollection<string>? ProfanityWords { get; set; }

        /// <summary>Maximum message length; longer messages are truncated. Default 500.</summary>
        public int MaxLength { get; set; } = 500;
    }

    /// <summary>
    /// Text chat by composition: channel-based (join/leave a channel by name), server-relayed, with optional length limits
    /// and a simple whole-word profanity filter. Rides the unified protocol on the <see cref="Channels.Chat"/> channel.
    /// </summary>
    public sealed class ChatServer
    {
        private readonly ChatOptions _options;
        // channel -> (peer id -> peer). peer -> its channels (for disconnect cleanup).
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, BasePeer>> _channels = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, BasePeer>>();
        private readonly ConcurrentDictionary<Guid, HashSet<string>> _memberChannels = new ConcurrentDictionary<Guid, HashSet<string>>();
        private readonly string[] _profanity;

        internal ChatServer(BaseServer server, ChatOptions options)
        {
            _options = options;
            _profanity = options.ProfanityWords is null ? Array.Empty<string>() : new List<string>(options.ProfanityWords).ToArray();
            server.PeerDisconnected += RemovePeer;
        }

        internal async Task HandleAsync(BasePeer peer, ChatOp op, string channel, string text)
        {
            switch (op)
            {
                case ChatOp.Join:
                    _channels.GetOrAdd(channel, _ => new ConcurrentDictionary<Guid, BasePeer>())[peer.CurrentPeerInfo.Id] = peer;
                    _memberChannels.GetOrAdd(peer.CurrentPeerInfo.Id, _ => new HashSet<string>()).Add(channel);
                    break;
                case ChatOp.Leave:
                    if (_channels.TryGetValue(channel, out var m)) m.TryRemove(peer.CurrentPeerInfo.Id, out _);
                    if (_memberChannels.TryGetValue(peer.CurrentPeerInfo.Id, out var set)) lock (set) set.Remove(channel);
                    break;
                case ChatOp.Message:
                    await RelayAsync(peer, channel, Sanitize(text)).ConfigureAwait(false);
                    break;
            }
        }

        private async Task RelayAsync(BasePeer sender, string channel, string text)
        {
            if (!_channels.TryGetValue(channel, out var members)) return;
            var evt = ChatWire.EncodeEvent(channel, sender.CurrentPeerInfo.Id.ToString("N"), text);
            foreach (var member in members.Values)
            {
                try { await member.PublishRawAsync(Channels.Chat, (ushort)ChatEvt.Message, evt).ConfigureAwait(false); } catch { /* dropping */ }
            }
        }

        private string Sanitize(string text)
        {
            text = text ?? "";
            if (text.Length > _options.MaxLength) text = text.Substring(0, _options.MaxLength);
            foreach (var word in _profanity)
            {
                if (string.IsNullOrEmpty(word)) continue;
                var idx = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                while (idx >= 0)
                {
                    text = text.Substring(0, idx) + new string('*', word.Length) + text.Substring(idx + word.Length);
                    idx = text.IndexOf(word, idx + word.Length, StringComparison.OrdinalIgnoreCase);
                }
            }
            return text;
        }

        private void RemovePeer(BasePeer peer)
        {
            if (!_memberChannels.TryRemove(peer.CurrentPeerInfo.Id, out var set)) return;
            lock (set)
                foreach (var channel in set)
                    if (_channels.TryGetValue(channel, out var m)) m.TryRemove(peer.CurrentPeerInfo.Id, out _);
        }
    }

    /// <summary>Client-side chat driver: join channels, send messages, receive them.</summary>
    public sealed class ChatClient
    {
        private readonly BaseClient _client;
        private readonly IDisposable _subscription;

        /// <summary>Raised on an incoming message (args: channel, sender player id, text).</summary>
        public event Action<string, string, string>? MessageReceived;

        internal ChatClient(BaseClient client)
        {
            _client = client;
            _subscription = _client.OnRaw(Channels.Chat, (ushort)ChatEvt.Message, body =>
            {
                var (channel, sender, text) = ChatWire.DecodeEvent(body);
                MessageReceived?.Invoke(channel, sender, text);
            });
        }

        /// <summary>Joins a channel.</summary>
        public Task JoinAsync(string channel) => Send(ChatOp.Join, channel, "");

        /// <summary>Leaves a channel.</summary>
        public Task LeaveAsync(string channel) => Send(ChatOp.Leave, channel, "");

        /// <summary>Sends a message to a channel.</summary>
        public Task SendAsync(string channel, string text) => Send(ChatOp.Message, channel, text);

        private Task Send(ChatOp op, string channel, string text)
            => _client.PostRawAsync(Channels.Chat, (ushort)op, ChatWire.EncodeCommand(channel, text));
    }

    internal static class ChatWire
    {
        public static byte[] EncodeCommand(string channel, string text)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) { w.Write(channel ?? ""); w.Write(text ?? ""); }
            return ms.ToArray();
        }

        public static (string channel, string text) DecodeCommand(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return (r.ReadString(), r.ReadString());
        }

        public static byte[] EncodeEvent(string channel, string sender, string text)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) { w.Write(channel ?? ""); w.Write(sender ?? ""); w.Write(text ?? ""); }
            return ms.ToArray();
        }

        public static (string channel, string sender, string text) DecodeEvent(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return (r.ReadString(), r.ReadString(), r.ReadString());
        }
    }

    internal static class ChatRegistry
    {
        private static readonly ConcurrentDictionary<BaseServer, ChatServer> Servers = new ConcurrentDictionary<BaseServer, ChatServer>();
        public static void RegisterServer(BaseServer server, ChatServer chat) => Servers[server] = chat;
        public static ChatServer? GetServer(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;
    }

    /// <summary>Attaches chat by composition — no base class.</summary>
    public static class ChatExtensions
    {
        /// <summary>Enables the chat server (channel relay + moderation).</summary>
        public static ChatServer UseChat(this BaseServer server, ChatOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            var chat = new ChatServer(server, options ?? new ChatOptions());
            ChatRegistry.RegisterServer(server, chat);
            return chat;
        }

        /// <summary>Enables the chat client (join/send + MessageReceived).</summary>
        public static ChatClient UseChat(this BaseClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return new ChatClient(client);
        }
    }

    /// <summary>Auto-discovered channel service for chat commands (fire-and-forget: join/leave/message).</summary>
    [ProtocolChannel(Channels.Chat)]
    public sealed class ChatChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var chat = ChatRegistry.GetServer(request.Peer.CurrentPeerInfo.Server);
            if (chat == null) return Task.CompletedTask;
            var (channel, text) = ChatWire.DecodeCommand(request.RawBody);
            return chat.HandleAsync(request.Peer, (ChatOp)request.Op, channel, text);
        }
    }

    /// <summary>One-time bootstrap so the chat channel service is discovered. Call at startup.</summary>
    public static class ChatRuntime
    {
        /// <summary>Ensures the chat layer is discoverable.</summary>
        public static void Enable() { _ = typeof(ChatChannelService); }
    }
}
