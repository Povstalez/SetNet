using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Inventory;
using SetNet.Protocol;

namespace SetNet.Mail
{
    /// <summary>Command operations (client → server) within the Mail protocol channel.</summary>
    internal enum MailOp : ushort { Send = 1, List = 2, Read = 3, Claim = 4, Delete = 5 }

    /// <summary>Push events (server → client) within the Mail protocol channel.</summary>
    internal enum MailEvt : ushort { Received = 10 }

    /// <summary>Thrown when a mail operation fails (unknown message, missing attachments, timeout).</summary>
    public sealed class MailException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public MailException(string message) : base(message) { }
    }

    /// <summary>One item attachment on a mail message.</summary>
    public sealed class MailAttachment
    {
        /// <summary>The attached item's id.</summary>
        public string ItemId { get; set; } = "";

        /// <summary>The attached quantity.</summary>
        public long Count { get; set; }

        /// <summary>Creates an empty attachment (for serialization).</summary>
        public MailAttachment() { }

        /// <summary>Creates an attachment of <paramref name="count"/> × <paramref name="itemId"/>.</summary>
        public MailAttachment(string itemId, long count) { ItemId = itemId; Count = count; }
    }

    /// <summary>A stored mail message (server-side model; also the shape returned to clients).</summary>
    public sealed class MailMessage
    {
        /// <summary>Unique message id.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Sender's player key ("SYSTEM" for server-sent mail).</summary>
        public string From { get; set; } = "";

        /// <summary>Subject line.</summary>
        public string Subject { get; set; } = "";

        /// <summary>Opaque body payload (text/JSON/serialized — the mail layer never inspects it).</summary>
        public byte[] Body { get; set; } = Array.Empty<byte>();

        /// <summary>Item attachments held in escrow until claimed.</summary>
        public List<MailAttachment> Attachments { get; set; } = new List<MailAttachment>();

        /// <summary>When the message was sent (UTC, unix milliseconds).</summary>
        public long SentUnixMs { get; set; }

        /// <summary>Whether the recipient has opened it.</summary>
        public bool Read { get; set; }

        /// <summary>Whether the attachments have been claimed into the recipient's inventory.</summary>
        public bool Claimed { get; set; }
    }

    // ---- store ----

    /// <summary>
    /// Persistence for mailboxes. The default is <see cref="MemoryMailStore"/> (in-process); supply a Redis/DB store
    /// so mail survives restarts and is readable on whichever node the recipient logs into.
    /// </summary>
    public interface IMailStore
    {
        /// <summary>Adds a message to a recipient's mailbox.</summary>
        Task AddAsync(string recipientKey, MailMessage message);

        /// <summary>Returns all messages in a recipient's mailbox (newest first is recommended but not required).</summary>
        Task<IReadOnlyList<MailMessage>> ListAsync(string recipientKey);

        /// <summary>Returns one message, or null when it's absent.</summary>
        Task<MailMessage?> GetAsync(string recipientKey, string messageId);

        /// <summary>Persists a mutated message (read/claimed flags).</summary>
        Task UpdateAsync(string recipientKey, MailMessage message);

        /// <summary>Removes a message; returns false when it was already gone.</summary>
        Task<bool> DeleteAsync(string recipientKey, string messageId);
    }

    /// <summary>In-process mailbox store. Fine for a single node / tests; swap for a shared store to persist or cluster.</summary>
    public sealed class MemoryMailStore : IMailStore
    {
        private readonly ConcurrentDictionary<string, Dictionary<string, MailMessage>> _boxes = new ConcurrentDictionary<string, Dictionary<string, MailMessage>>();

        private Dictionary<string, MailMessage> Box(string key) => _boxes.GetOrAdd(key ?? "", _ => new Dictionary<string, MailMessage>());

        /// <inheritdoc/>
        public Task AddAsync(string recipientKey, MailMessage message)
        {
            var box = Box(recipientKey);
            lock (box) box[message.Id] = message;
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<MailMessage>> ListAsync(string recipientKey)
        {
            var box = Box(recipientKey);
            List<MailMessage> list;
            lock (box) list = box.Values.OrderByDescending(m => m.SentUnixMs).ToList();
            return Task.FromResult<IReadOnlyList<MailMessage>>(list);
        }

        /// <inheritdoc/>
        public Task<MailMessage?> GetAsync(string recipientKey, string messageId)
        {
            var box = Box(recipientKey);
            lock (box) return Task.FromResult(box.TryGetValue(messageId, out var m) ? m : null);
        }

        /// <inheritdoc/>
        public Task UpdateAsync(string recipientKey, MailMessage message)
        {
            var box = Box(recipientKey);
            lock (box) box[message.Id] = message;
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<bool> DeleteAsync(string recipientKey, string messageId)
        {
            var box = Box(recipientKey);
            lock (box) return Task.FromResult(box.Remove(messageId));
        }
    }

    /// <summary>Settings for the mail service.</summary>
    public sealed class MailOptions
    {
        /// <summary>Maps a connected peer to its stable player key (default = connection id; override for durable mailboxes).</summary>
        public Func<BasePeer, string> PlayerKey { get; set; } = peer => peer.CurrentPeerInfo.Id.ToString();

        /// <summary>Max attachments per message (default 8). Sends exceeding it are rejected.</summary>
        public int MaxAttachments { get; set; } = 8;

        /// <summary>Max body bytes per message (default 64 KB). Larger sends are rejected.</summary>
        public int MaxBodyBytes { get; set; } = 64 * 1024;
    }

    // ---- wire ----

    /// <summary>Decoded mail command body (op and correlation live in the protocol envelope).</summary>
    internal sealed class MailCommand
    {
        public string ToKey = "";
        public string MessageId = "";
        public string Subject = "";
        public byte[] Body = Array.Empty<byte>();
        public List<MailAttachment> Attachments = new List<MailAttachment>();
    }

    /// <summary>Body codecs for the Mail channel (payload only; op/correlation are in the envelope).</summary>
    internal static class MailCodec
    {
        public static byte[] EncodeCommand(MailCommand cmd)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(cmd.ToKey ?? "");
            w.Write(cmd.MessageId ?? "");
            w.Write(cmd.Subject ?? "");
            w.Write(cmd.Body?.Length ?? 0);
            if (cmd.Body != null) w.Write(cmd.Body);
            WriteAttachments(w, cmd.Attachments);
            return ms.ToArray();
        }

        public static MailCommand DecodeCommand(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var cmd = new MailCommand { ToKey = r.ReadString(), MessageId = r.ReadString(), Subject = r.ReadString() };
            var len = r.ReadInt32();
            cmd.Body = len > 0 ? r.ReadBytes(len) : Array.Empty<byte>();
            cmd.Attachments = ReadAttachments(r);
            return cmd;
        }

        public static byte[] EncodeReply(string messageId, List<MailMessage> messages)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(messageId ?? "");
            w.Write(messages?.Count ?? 0);
            if (messages != null) foreach (var m in messages) WriteMessage(w, m);
            return ms.ToArray();
        }

        public static (string messageId, List<MailMessage> messages) DecodeReply(byte[] data)
        {
            if (data == null || data.Length == 0) return ("", new List<MailMessage>());
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var id = r.ReadString();
            var count = r.ReadInt32();
            var list = new List<MailMessage>(count);
            for (var i = 0; i < count; i++) list.Add(ReadMessage(r));
            return (id, list);
        }

        public static byte[] EncodeMessage(MailMessage m)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WriteMessage(w, m);
            return ms.ToArray();
        }

        public static MailMessage DecodeMessage(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return ReadMessage(r);
        }

        public static void WriteAttachments(BinaryWriter w, List<MailAttachment> attachments)
        {
            w.Write(attachments?.Count ?? 0);
            if (attachments != null) foreach (var a in attachments) { w.Write(a.ItemId ?? ""); w.Write(a.Count); }
        }

        public static List<MailAttachment> ReadAttachments(BinaryReader r)
        {
            var count = r.ReadInt32();
            var list = new List<MailAttachment>(count);
            for (var i = 0; i < count; i++) list.Add(new MailAttachment(r.ReadString(), r.ReadInt64()));
            return list;
        }

        public static void WriteMessage(BinaryWriter w, MailMessage m)
        {
            w.Write(m.Id ?? "");
            w.Write(m.From ?? "");
            w.Write(m.Subject ?? "");
            w.Write(m.Body?.Length ?? 0);
            if (m.Body != null) w.Write(m.Body);
            WriteAttachments(w, m.Attachments);
            w.Write(m.SentUnixMs);
            w.Write(m.Read);
            w.Write(m.Claimed);
        }

        public static MailMessage ReadMessage(BinaryReader r)
        {
            var m = new MailMessage { Id = r.ReadString(), From = r.ReadString(), Subject = r.ReadString() };
            var len = r.ReadInt32();
            m.Body = len > 0 ? r.ReadBytes(len) : Array.Empty<byte>();
            m.Attachments = ReadAttachments(r);
            m.SentUnixMs = r.ReadInt64();
            m.Read = r.ReadBoolean();
            m.Claimed = r.ReadBoolean();
            return m;
        }
    }

    // ---- client ----

    /// <summary>
    /// Client-side mail driver, attached by <see cref="MailClientExtensions.UseMail"/>. Send mail (with optional item
    /// attachments) to another player whether they're online or not, list and read your mailbox, and claim
    /// attachments into your inventory. New mail that arrives while you're online is pushed via <see cref="Received"/>.
    /// Rides the unified protocol on the <see cref="Channels.Mail"/> channel.
    /// </summary>
    public sealed class MailClient
    {
        private readonly BaseClient _client;
        private readonly IDisposable _subscription;

        /// <summary>Raised when a new message arrives while this client is connected.</summary>
        public event Action<MailMessage>? Received;

        internal MailClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _subscription = _client.OnRaw(Channels.Mail, (ushort)MailEvt.Received,
                body => Received?.Invoke(MailCodec.DecodeMessage(body)));
        }

        /// <summary>Sends mail to another player by key. Attachments are escrowed from your inventory now and delivered on claim; returns the new message id.</summary>
        public async Task<string> SendAsync(string toPlayerKey, string subject, byte[]? body = null, IEnumerable<MailAttachment>? attachments = null)
        {
            var cmd = new MailCommand
            {
                ToKey = toPlayerKey,
                Subject = subject ?? "",
                Body = body ?? Array.Empty<byte>(),
                Attachments = attachments?.ToList() ?? new List<MailAttachment>(),
            };
            var (messageId, _) = await SendCommand(MailOp.Send, cmd).ConfigureAwait(false);
            return messageId;
        }

        /// <summary>Lists your mailbox (bodies included; attachments listed but not yet claimed).</summary>
        public async Task<IReadOnlyList<MailMessage>> ListAsync()
        {
            var (_, messages) = await SendCommand(MailOp.List, new MailCommand()).ConfigureAwait(false);
            return messages;
        }

        /// <summary>Marks a message read and returns it.</summary>
        public async Task<MailMessage> ReadAsync(string messageId)
        {
            var (_, messages) = await SendCommand(MailOp.Read, new MailCommand { MessageId = messageId }).ConfigureAwait(false);
            if (messages.Count == 0) throw new MailException("No such message.");
            return messages[0];
        }

        /// <summary>Claims a message's attachments into your inventory (idempotent — claiming twice grants once).</summary>
        public Task ClaimAsync(string messageId)
            => SendCommand(MailOp.Claim, new MailCommand { MessageId = messageId });

        /// <summary>Deletes a message. Unclaimed attachments are returned to the sender.</summary>
        public Task DeleteAsync(string messageId)
            => SendCommand(MailOp.Delete, new MailCommand { MessageId = messageId });

        private async Task<(string messageId, List<MailMessage> messages)> SendCommand(MailOp op, MailCommand cmd)
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.Mail, (ushort)op, MailCodec.EncodeCommand(cmd)).ConfigureAwait(false);
                return MailCodec.DecodeReply(body);
            }
            catch (ProtocolException ex) { throw new MailException(ex.Message); }
            catch (TimeoutException) { throw new MailException("Mail command timed out."); }
        }
    }

    // ---- server ----

    /// <summary>
    /// Server-side mail hub, attached by <see cref="MailServerExtensions.UseMail"/>. Stores mailboxes, escrows item
    /// attachments from the sender's inventory at send time (so attachments can't be duplicated), delivers them to
    /// the recipient's inventory on claim, and pushes new mail to online recipients. Game logic can also send system
    /// mail directly via <see cref="SendSystemAsync"/>.
    /// </summary>
    public sealed class MailServer
    {
        private static readonly ConcurrentDictionary<BaseServer, MailServer> Servers = new ConcurrentDictionary<BaseServer, MailServer>();

        private readonly IMailStore _store;
        private readonly InventoryServer? _inventory;
        private readonly MailOptions _options;
        private readonly ConcurrentDictionary<string, BasePeer> _online = new ConcurrentDictionary<string, BasePeer>();

        internal MailServer(IMailStore store, InventoryServer? inventory, MailOptions options)
        {
            _store = store;
            _inventory = inventory;
            _options = options;
        }

        internal static MailServer Enable(BaseServer server, IMailStore? store, InventoryServer? inventory, MailOptions? options)
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new MailServer(store ?? new MemoryMailStore(), inventory, options ?? new MailOptions());
                s.PeerConnected += peer => hub._online[hub._options.PlayerKey(peer)] = peer;
                s.PeerDisconnected += peer =>
                {
                    var key = hub._options.PlayerKey(peer);
                    if (hub._online.TryGetValue(key, out var current) && ReferenceEquals(current, peer))
                        hub._online.TryRemove(key, out _);
                };
                return hub;
            });

        internal static MailServer? For(BaseServer? server)
            => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Sends system mail (from "SYSTEM") to a player. Attachments are minted, not escrowed (server-granted rewards).</summary>
        public async Task<string> SendSystemAsync(string toPlayerKey, string subject, byte[]? body = null, IEnumerable<MailAttachment>? attachments = null)
        {
            var message = new MailMessage
            {
                From = "SYSTEM",
                Subject = subject ?? "",
                Body = body ?? Array.Empty<byte>(),
                Attachments = attachments?.ToList() ?? new List<MailAttachment>(),
                SentUnixMs = NowMs(),
            };
            await Deliver(toPlayerKey, message).ConfigureAwait(false);
            return message.Id;
        }

        internal async Task HandleAsync(ChannelRequest request)
        {
            var me = _options.PlayerKey(request.Peer);
            var cmd = MailCodec.DecodeCommand(request.RawBody);
            switch ((MailOp)request.Op)
            {
                case MailOp.Send: await Send(request, me, cmd); break;
                case MailOp.List: await ListReply(request, me); break;
                case MailOp.Read: await Read(request, me, cmd); break;
                case MailOp.Claim: await Claim(request, me, cmd); break;
                case MailOp.Delete: await Delete(request, me, cmd); break;
            }
        }

        private async Task Send(ChannelRequest request, string me, MailCommand cmd)
        {
            if (string.IsNullOrEmpty(cmd.ToKey) || cmd.ToKey == me) throw new ProtocolException("Invalid recipient.");
            if (cmd.Body.Length > _options.MaxBodyBytes) throw new ProtocolException("Body too large.");
            if (cmd.Attachments.Count > _options.MaxAttachments) throw new ProtocolException("Too many attachments.");
            if (cmd.Attachments.Count > 0 && _inventory == null) throw new ProtocolException("Attachments require a configured inventory.");

            // Escrow attachments out of the sender's inventory now so they can't be duped; roll back on any shortfall.
            var escrowed = new List<MailAttachment>();
            foreach (var a in cmd.Attachments)
            {
                if (a.Count <= 0 || string.IsNullOrEmpty(a.ItemId)) continue;
                if (await _inventory!.TryRevokeAsync(me, a.ItemId, a.Count).ConfigureAwait(false)) escrowed.Add(a);
                else
                {
                    foreach (var back in escrowed) await _inventory!.GrantAsync(me, back.ItemId, back.Count).ConfigureAwait(false);
                    throw new ProtocolException($"You don't have {a.Count} × {a.ItemId}.");
                }
            }

            var message = new MailMessage
            {
                From = me,
                Subject = cmd.Subject,
                Body = cmd.Body,
                Attachments = escrowed,
                SentUnixMs = NowMs(),
            };
            await Deliver(cmd.ToKey, message).ConfigureAwait(false);
            await request.ReplyRawAsync(MailCodec.EncodeReply(message.Id, new List<MailMessage>())).ConfigureAwait(false);
        }

        private async Task Deliver(string recipientKey, MailMessage message)
        {
            await _store.AddAsync(recipientKey, message).ConfigureAwait(false);
            if (_online.TryGetValue(recipientKey, out var recipientPeer))
            {
                try { await recipientPeer.PublishRawAsync(Channels.Mail, (ushort)MailEvt.Received, MailCodec.EncodeMessage(message)).ConfigureAwait(false); }
                catch { /* dropped */ }
            }
        }

        private async Task ListReply(ChannelRequest request, string me)
        {
            var list = await _store.ListAsync(me).ConfigureAwait(false);
            await request.ReplyRawAsync(MailCodec.EncodeReply("", list.ToList())).ConfigureAwait(false);
        }

        private async Task Read(ChannelRequest request, string me, MailCommand cmd)
        {
            var message = await _store.GetAsync(me, cmd.MessageId).ConfigureAwait(false);
            if (message == null) throw new ProtocolException("No such message.");
            if (!message.Read) { message.Read = true; await _store.UpdateAsync(me, message).ConfigureAwait(false); }
            await request.ReplyRawAsync(MailCodec.EncodeReply(message.Id, new List<MailMessage> { message })).ConfigureAwait(false);
        }

        private async Task Claim(ChannelRequest request, string me, MailCommand cmd)
        {
            var message = await _store.GetAsync(me, cmd.MessageId).ConfigureAwait(false);
            if (message == null) throw new ProtocolException("No such message.");
            if (!message.Claimed && message.Attachments.Count > 0)
            {
                if (_inventory == null) throw new ProtocolException("Inventory not configured.");
                foreach (var a in message.Attachments)
                    await _inventory.GrantAsync(me, a.ItemId, a.Count).ConfigureAwait(false);
            }
            message.Claimed = true;
            message.Read = true;
            await _store.UpdateAsync(me, message).ConfigureAwait(false);
            await request.ReplyRawAsync(MailCodec.EncodeReply(message.Id, new List<MailMessage>())).ConfigureAwait(false);
        }

        private async Task Delete(ChannelRequest request, string me, MailCommand cmd)
        {
            var message = await _store.GetAsync(me, cmd.MessageId).ConfigureAwait(false);
            // Return unclaimed attachments to the sender so items are never destroyed by a delete.
            if (message != null && !message.Claimed && message.Attachments.Count > 0 && _inventory != null && message.From != "SYSTEM")
                foreach (var a in message.Attachments)
                    await _inventory.GrantAsync(message.From, a.ItemId, a.Count).ConfigureAwait(false);

            await _store.DeleteAsync(me, cmd.MessageId).ConfigureAwait(false);
            await request.ReplyRawAsync(MailCodec.EncodeReply(cmd.MessageId, new List<MailMessage>())).ConfigureAwait(false);
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    // ---- auto-discovered channel service ----

    /// <summary>Auto-discovered channel service for mail commands.</summary>
    [ProtocolChannel(Channels.Mail)]
    public sealed class MailChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = MailServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("mail is not configured on this server");
            return hub.HandleAsync(request);
        }
    }

    // ---- composition entry points ----

    /// <summary>Attaches the mail hub to a server by composition.</summary>
    public static class MailServerExtensions
    {
        /// <summary>Enables the server-side mail hub. Pass the <see cref="InventoryServer"/> from <c>UseInventory</c> to enable item attachments.</summary>
        public static MailServer UseMail(this BaseServer server, IMailStore? store = null, InventoryServer? inventory = null, MailOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return MailServer.Enable(server, store, inventory, options);
        }
    }

    /// <summary>Attaches a mail driver to a client by composition.</summary>
    public static class MailClientExtensions
    {
        /// <summary>Enables client-side mail; returns the driver (send/list/read/claim/delete + <c>Received</c>).</summary>
        public static MailClient UseMail(this BaseClient client) => new MailClient(client);
    }

    /// <summary>One-time bootstrap so the mail channel service is discovered. Call at startup.</summary>
    public static class MailRuntime
    {
        /// <summary>Ensures the mail layer is discoverable.</summary>
        public static void Enable() { _ = typeof(MailChannelService); }
    }
}
