using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.Notifications
{
    /// <summary>Push events (server → client) within the Notifications channel.</summary>
    internal enum NotifyEvt : ushort { Notify = 10 }

    /// <summary>One notification / toast. All fields are your own strings; <see cref="Data"/> is an opaque payload.</summary>
    public sealed class Notification
    {
        /// <summary>A category you define ("info", "warning", "achievement", "mail"…). Drives client presentation.</summary>
        public string Kind { get; set; } = "info";
        /// <summary>Short title.</summary>
        public string Title { get; set; } = "";
        /// <summary>Body text.</summary>
        public string Body { get; set; } = "";
        /// <summary>Optional opaque payload (a reward id, a deep-link…).</summary>
        public byte[]? Data { get; set; }

        /// <summary>Creates an empty notification.</summary>
        public Notification() { }
        /// <summary>Creates a notification.</summary>
        public Notification(string kind, string title, string body, byte[]? data = null)
        { Kind = kind; Title = title; Body = body; Data = data; }
    }

    /// <summary>Queues notifications for offline players so they arrive on the next connect. Default is in-process.</summary>
    public interface INotificationStore
    {
        /// <summary>Stores a notification for a player who was offline.</summary>
        Task EnqueueAsync(string playerKey, Notification notification);
        /// <summary>Removes and returns all pending notifications for a player (called on connect).</summary>
        Task<IReadOnlyList<Notification>> DrainAsync(string playerKey);
    }

    /// <summary>In-process offline queue. Swap for a Redis/DB store to survive restarts / share across nodes.</summary>
    public sealed class MemoryNotificationStore : INotificationStore
    {
        private readonly ConcurrentDictionary<string, List<Notification>> _pending = new ConcurrentDictionary<string, List<Notification>>();

        /// <inheritdoc/>
        public Task EnqueueAsync(string playerKey, Notification notification)
        {
            var list = _pending.GetOrAdd(playerKey ?? "", _ => new List<Notification>());
            lock (list) list.Add(notification);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<Notification>> DrainAsync(string playerKey)
        {
            if (!_pending.TryRemove(playerKey ?? "", out var list)) return Task.FromResult<IReadOnlyList<Notification>>(Array.Empty<Notification>());
            lock (list) return Task.FromResult<IReadOnlyList<Notification>>(list.ToArray());
        }
    }

    /// <summary>Settings for the notification hub.</summary>
    public sealed class NotificationOptions
    {
        /// <summary>Maps a connected peer to its stable player key (default = connection id).</summary>
        public Func<BasePeer, string> PlayerKey { get; set; } = peer => peer.CurrentPeerInfo.Id.ToString();
    }

    internal static class NotifyCodec
    {
        public static byte[] Encode(Notification n)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(n.Kind ?? "");
            w.Write(n.Title ?? "");
            w.Write(n.Body ?? "");
            var data = n.Data ?? Array.Empty<byte>();
            w.Write(data.Length);
            w.Write(data);
            return ms.ToArray();
        }

        public static Notification Decode(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            var kind = r.ReadString();
            var title = r.ReadString();
            var text = r.ReadString();
            var len = r.ReadInt32();
            var data = len > 0 ? r.ReadBytes(len) : null;
            return new Notification(kind, title, text, data);
        }
    }

    /// <summary>Client-side notifications driver (from <see cref="NotificationClientExtensions.UseNotifications"/>): raises <see cref="Received"/>.</summary>
    public sealed class NotificationClient
    {
        private readonly IDisposable _subscription;

        /// <summary>Raised when the server pushes a notification to this player.</summary>
        public event Action<Notification>? Received;

        internal NotificationClient(BaseClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            _subscription = client.OnRaw(Channels.Notifications, (ushort)NotifyEvt.Notify,
                body => Received?.Invoke(NotifyCodec.Decode(body)));
        }
    }

    /// <summary>
    /// Server-side notification hub (from <see cref="NotificationServerExtensions.UseNotifications"/>). Push to one
    /// player or broadcast to all; offline players' notifications are queued and flushed when they reconnect.
    /// </summary>
    public sealed class NotificationServer
    {
        private static readonly ConcurrentDictionary<BaseServer, NotificationServer> Servers = new ConcurrentDictionary<BaseServer, NotificationServer>();

        private readonly NotificationOptions _options;
        private readonly INotificationStore _store;
        private readonly ConcurrentDictionary<string, BasePeer> _online = new ConcurrentDictionary<string, BasePeer>();

        internal NotificationServer(INotificationStore store, NotificationOptions options) { _store = store; _options = options; }

        internal static NotificationServer Enable(BaseServer server, INotificationStore? store, NotificationOptions? options)
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new NotificationServer(store ?? new MemoryNotificationStore(), options ?? new NotificationOptions());
                s.PeerConnected += peer =>
                {
                    var key = hub._options.PlayerKey(peer);
                    hub._online[key] = peer;
                    _ = hub.FlushAsync(key, peer);
                };
                s.PeerDisconnected += peer =>
                {
                    var key = hub._options.PlayerKey(peer);
                    if (hub._online.TryGetValue(key, out var cur) && ReferenceEquals(cur, peer)) hub._online.TryRemove(key, out _);
                };
                return hub;
            });

        /// <summary>Sends a notification to a player — pushed if online, otherwise queued for their next connect.</summary>
        public async Task NotifyAsync(string playerKey, Notification notification)
        {
            if (_online.TryGetValue(playerKey, out var peer))
            {
                try { await peer.PublishRawAsync(Channels.Notifications, (ushort)NotifyEvt.Notify, NotifyCodec.Encode(notification)).ConfigureAwait(false); return; }
                catch { /* fall through to queue if the push fails */ }
            }
            await _store.EnqueueAsync(playerKey, notification).ConfigureAwait(false);
        }

        /// <summary>Pushes a notification to every currently-online player (not queued for offline players).</summary>
        public async Task BroadcastAsync(Notification notification)
        {
            var body = NotifyCodec.Encode(notification);
            foreach (var peer in _online.Values)
                try { await peer.PublishRawAsync(Channels.Notifications, (ushort)NotifyEvt.Notify, body).ConfigureAwait(false); } catch { }
        }

        private async Task FlushAsync(string playerKey, BasePeer peer)
        {
            var pending = await _store.DrainAsync(playerKey).ConfigureAwait(false);
            foreach (var n in pending)
                try { await peer.PublishRawAsync(Channels.Notifications, (ushort)NotifyEvt.Notify, NotifyCodec.Encode(n)).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Attaches the notification hub to a server.</summary>
    public static class NotificationServerExtensions
    {
        /// <summary>Enables the server-side notification hub; returns it so game logic can push/broadcast.</summary>
        public static NotificationServer UseNotifications(this BaseServer server, INotificationStore? store = null, NotificationOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return NotificationServer.Enable(server, store, options);
        }
    }

    /// <summary>Attaches a notification driver to a client.</summary>
    public static class NotificationClientExtensions
    {
        /// <summary>Enables client-side notifications; returns the driver (<c>Received</c>).</summary>
        public static NotificationClient UseNotifications(this BaseClient client) => new NotificationClient(client);
    }

    /// <summary>One-time bootstrap for symmetry (push-only channel needs no server-side discovery). Safe to call at startup.</summary>
    public static class NotificationRuntime
    {
        /// <summary>No-op marker so callers can Enable() like other modules.</summary>
        public static void Enable() { }
    }
}
