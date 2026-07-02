using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Inventory;

namespace SetNet.Mail
{
    /// <summary>Reserved wire types for the mail service. Don't reuse these ids for application messages.</summary>
    public static class MailTypes
    {
        /// <summary>Client → server: send/list/read/claim/delete command.</summary>
        public const ushort Command = ushort.MaxValue - 52;   // 65483

        /// <summary>Server → client: correlated reply.</summary>
        public const ushort Reply = ushort.MaxValue - 53;     // 65482

        /// <summary>Server → client: push event when new mail arrives while online.</summary>
        public const ushort Event = ushort.MaxValue - 54;     // 65481
    }

    internal enum MailOp : byte { Send = 0, List = 1, Read = 2, Claim = 3, Delete = 4 }

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

    internal sealed class MailCommand
    {
        public int CorrelationId;
        public MailOp Op;
        public string ToKey = "";
        public string MessageId = "";
        public string Subject = "";
        public byte[] Body = Array.Empty<byte>();
        public List<MailAttachment> Attachments = new List<MailAttachment>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId);
            w.Write((byte)Op);
            w.Write(ToKey ?? "");
            w.Write(MessageId ?? "");
            w.Write(Subject ?? "");
            w.Write(Body?.Length ?? 0);
            if (Body != null) w.Write(Body);
            MailCodec.WriteAttachments(w, Attachments);
            return ms.ToArray();
        }

        public static MailCommand Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var cmd = new MailCommand
            {
                CorrelationId = r.ReadInt32(),
                Op = (MailOp)r.ReadByte(),
                ToKey = r.ReadString(),
                MessageId = r.ReadString(),
                Subject = r.ReadString(),
            };
            var len = r.ReadInt32();
            cmd.Body = len > 0 ? r.ReadBytes(len) : Array.Empty<byte>();
            cmd.Attachments = MailCodec.ReadAttachments(r);
            return cmd;
        }
    }

    internal sealed class MailReply
    {
        public int CorrelationId;
        public bool Success;
        public string Error = "";
        public string MessageId = "";           // Send: new id
        public List<MailMessage> Messages = new List<MailMessage>();   // List / Read

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId);
            w.Write(Success);
            w.Write(Error ?? "");
            w.Write(MessageId ?? "");
            w.Write(Messages.Count);
            foreach (var m in Messages) MailCodec.WriteMessage(w, m);
            return ms.ToArray();
        }

        public static MailReply Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var reply = new MailReply
            {
                CorrelationId = r.ReadInt32(),
                Success = r.ReadBoolean(),
                Error = r.ReadString(),
                MessageId = r.ReadString(),
            };
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++) reply.Messages.Add(MailCodec.ReadMessage(r));
            return reply;
        }
    }

    internal static class MailCodec
    {
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

    internal static class MailRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<MailReply>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<MailReply>>();
        private static readonly ConcurrentDictionary<MailClient, byte> Clients
            = new ConcurrentDictionary<MailClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<MailReply> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, MailReply reply) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(reply); }
        public static void RegisterClient(MailClient c) => Clients[c] = 0;
        public static void DispatchEvent(MailMessage m) { foreach (var c in Clients.Keys) c.OnReceived(m); }
    }

    /// <summary>
    /// Client-side mail driver, attached by <see cref="MailClientExtensions.UseMail"/>. Send mail (with optional item
    /// attachments) to another player whether they're online or not, list and read your mailbox, and claim
    /// attachments into your inventory. New mail that arrives while you're online is pushed via <see cref="Received"/>.
    /// </summary>
    public sealed class MailClient
    {
        private readonly BaseClient _client;

        /// <summary>Raised when a new message arrives while this client is connected.</summary>
        public event Action<MailMessage>? Received;

        internal MailClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            MailRegistry.RegisterClient(this);
        }

        /// <summary>Sends mail to another player by key. Attachments are escrowed from your inventory now and delivered on claim; returns the new message id.</summary>
        public async Task<string> SendAsync(string toPlayerKey, string subject, byte[]? body = null, IEnumerable<MailAttachment>? attachments = null)
        {
            var cmd = new MailCommand
            {
                Op = MailOp.Send,
                ToKey = toPlayerKey,
                Subject = subject ?? "",
                Body = body ?? Array.Empty<byte>(),
                Attachments = attachments?.ToList() ?? new List<MailAttachment>(),
            };
            var reply = await SendCommand(cmd).ConfigureAwait(false);
            return reply.MessageId;
        }

        /// <summary>Lists your mailbox (bodies included; attachments listed but not yet claimed).</summary>
        public async Task<IReadOnlyList<MailMessage>> ListAsync()
        {
            var reply = await SendCommand(new MailCommand { Op = MailOp.List }).ConfigureAwait(false);
            return reply.Messages;
        }

        /// <summary>Marks a message read and returns it.</summary>
        public async Task<MailMessage> ReadAsync(string messageId)
        {
            var reply = await SendCommand(new MailCommand { Op = MailOp.Read, MessageId = messageId }).ConfigureAwait(false);
            if (reply.Messages.Count == 0) throw new MailException("No such message.");
            return reply.Messages[0];
        }

        /// <summary>Claims a message's attachments into your inventory (idempotent — claiming twice grants once).</summary>
        public Task ClaimAsync(string messageId)
            => SendCommand(new MailCommand { Op = MailOp.Claim, MessageId = messageId });

        /// <summary>Deletes a message. Unclaimed attachments are returned to the sender.</summary>
        public Task DeleteAsync(string messageId)
            => SendCommand(new MailCommand { Op = MailOp.Delete, MessageId = messageId });

        private async Task<MailReply> SendCommand(MailCommand cmd)
        {
            var id = MailRegistry.NextId();
            cmd.CorrelationId = id;
            var tcs = new TaskCompletionSource<MailReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            MailRegistry.Register(id, tcs);
            try
            {
                await _client.SendAsync(MailTypes.Command, cmd.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    MailReply reply;
                    try { reply = await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new MailException("Mail command timed out."); }
                    if (!reply.Success) throw new MailException(reply.Error);
                    return reply;
                }
            }
            finally { MailRegistry.Remove(id); }
        }

        internal void OnReceived(MailMessage m) => Received?.Invoke(m);
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

        internal async Task OnCommand(BasePeer peer, MailCommand cmd)
        {
            var me = _options.PlayerKey(peer);
            switch (cmd.Op)
            {
                case MailOp.Send: await Send(peer, me, cmd); break;
                case MailOp.List: await ListReply(peer, me, cmd.CorrelationId); break;
                case MailOp.Read: await Read(peer, me, cmd); break;
                case MailOp.Claim: await Claim(peer, me, cmd); break;
                case MailOp.Delete: await Delete(peer, me, cmd); break;
            }
        }

        private async Task Send(BasePeer peer, string me, MailCommand cmd)
        {
            if (string.IsNullOrEmpty(cmd.ToKey) || cmd.ToKey == me)
            { await Reply(peer, cmd.CorrelationId, false, "Invalid recipient."); return; }
            if (cmd.Body.Length > _options.MaxBodyBytes)
            { await Reply(peer, cmd.CorrelationId, false, "Body too large."); return; }
            if (cmd.Attachments.Count > _options.MaxAttachments)
            { await Reply(peer, cmd.CorrelationId, false, "Too many attachments."); return; }
            if (cmd.Attachments.Count > 0 && _inventory == null)
            { await Reply(peer, cmd.CorrelationId, false, "Attachments require a configured inventory."); return; }

            // Escrow attachments out of the sender's inventory now so they can't be duped; roll back on any shortfall.
            var escrowed = new List<MailAttachment>();
            foreach (var a in cmd.Attachments)
            {
                if (a.Count <= 0 || string.IsNullOrEmpty(a.ItemId)) continue;
                if (await _inventory!.TryRevokeAsync(me, a.ItemId, a.Count).ConfigureAwait(false)) escrowed.Add(a);
                else
                {
                    foreach (var back in escrowed) await _inventory!.GrantAsync(me, back.ItemId, back.Count).ConfigureAwait(false);
                    await Reply(peer, cmd.CorrelationId, false, $"You don't have {a.Count} × {a.ItemId}.");
                    return;
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
            await Reply(peer, cmd.CorrelationId, true, "", message.Id);
        }

        private async Task Deliver(string recipientKey, MailMessage message)
        {
            await _store.AddAsync(recipientKey, message).ConfigureAwait(false);
            if (_online.TryGetValue(recipientKey, out var recipientPeer))
            {
                using var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms)) MailCodec.WriteMessage(w, message);
                try { await recipientPeer.SendAsync(MailTypes.Event, ms.ToArray(), DeliveryMethod.Reliable).ConfigureAwait(false); } catch { /* dropped */ }
            }
        }

        private async Task ListReply(BasePeer peer, string me, int correlationId)
        {
            var list = await _store.ListAsync(me).ConfigureAwait(false);
            await Reply(peer, correlationId, true, "", "", list.ToList());
        }

        private async Task Read(BasePeer peer, string me, MailCommand cmd)
        {
            var message = await _store.GetAsync(me, cmd.MessageId).ConfigureAwait(false);
            if (message == null) { await Reply(peer, cmd.CorrelationId, false, "No such message."); return; }
            if (!message.Read) { message.Read = true; await _store.UpdateAsync(me, message).ConfigureAwait(false); }
            await Reply(peer, cmd.CorrelationId, true, "", message.Id, new List<MailMessage> { message });
        }

        private async Task Claim(BasePeer peer, string me, MailCommand cmd)
        {
            var message = await _store.GetAsync(me, cmd.MessageId).ConfigureAwait(false);
            if (message == null) { await Reply(peer, cmd.CorrelationId, false, "No such message."); return; }
            if (!message.Claimed && message.Attachments.Count > 0)
            {
                if (_inventory == null) { await Reply(peer, cmd.CorrelationId, false, "Inventory not configured."); return; }
                foreach (var a in message.Attachments)
                    await _inventory.GrantAsync(me, a.ItemId, a.Count).ConfigureAwait(false);
            }
            message.Claimed = true;
            message.Read = true;
            await _store.UpdateAsync(me, message).ConfigureAwait(false);
            await Reply(peer, cmd.CorrelationId, true, "", message.Id);
        }

        private async Task Delete(BasePeer peer, string me, MailCommand cmd)
        {
            var message = await _store.GetAsync(me, cmd.MessageId).ConfigureAwait(false);
            // Return unclaimed attachments to the sender so items are never destroyed by a delete.
            if (message != null && !message.Claimed && message.Attachments.Count > 0 && _inventory != null && message.From != "SYSTEM")
                foreach (var a in message.Attachments)
                    await _inventory.GrantAsync(message.From, a.ItemId, a.Count).ConfigureAwait(false);

            await _store.DeleteAsync(me, cmd.MessageId).ConfigureAwait(false);
            await Reply(peer, cmd.CorrelationId, true, "", cmd.MessageId);
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static Task Reply(BasePeer peer, int corr, bool ok, string err, string messageId = "", List<MailMessage>? messages = null)
        {
            var reply = new MailReply { CorrelationId = corr, Success = ok, Error = err, MessageId = messageId, Messages = messages ?? new List<MailMessage>() };
            try { return peer.SendAsync(MailTypes.Reply, reply.Encode(), DeliveryMethod.Reliable); } catch { return Task.CompletedTask; }
        }
    }

    // ---- auto-discovered handlers ----

    /// <summary>Auto-discovered server handler for mail commands.</summary>
    [MessageHandler(MailTypes.Command)]
    public sealed class MailCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = MailServer.For(peer.CurrentPeerInfo.Server);
            return hub?.OnCommand(peer, MailCommand.Decode(data)) ?? Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for correlated mail replies.</summary>
    [MessageHandler(MailTypes.Reply)]
    public sealed class MailReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var r = MailReply.Decode(data); MailRegistry.Complete(r.CorrelationId, r); return Task.CompletedTask; }
    }

    /// <summary>Auto-discovered client handler for new-mail push events.</summary>
    [MessageHandler(MailTypes.Event)]
    public sealed class MailEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            MailRegistry.DispatchEvent(MailCodec.ReadMessage(r));
            return Task.CompletedTask;
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

    /// <summary>One-time bootstrap so the mail handlers are discovered. Call at startup.</summary>
    public static class MailRuntime
    {
        /// <summary>Ensures the mail layer is discoverable.</summary>
        public static void Enable() { _ = MailTypes.Command; }
    }
}
