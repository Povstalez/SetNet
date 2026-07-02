using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Messaging;
using SetNet.Protocol;

namespace SetNet.Rooms
{
    /// <summary>
    /// Client-side rooms driver, attached by <see cref="RoomsClientExtensions.UseRooms"/>. Create/join a room by
    /// code, broadcast to it, and receive player-joined/left and message events — all by composition, alongside
    /// your regular messages. Rides the unified protocol on the <see cref="Channels.Rooms"/> channel: requests use
    /// the shared correlation mechanism and events the shared subscription mechanism (no per-module plumbing).
    /// </summary>
    public sealed class RoomsClient
    {
        private readonly BaseClient _client;
        private readonly object _gate = new object();
        private string? _code;
        private string _ownId = "";
        private readonly HashSet<string> _members = new HashSet<string>();
        private readonly ConcurrentDictionary<ushort, Action<string, byte[]>> _typed = new ConcurrentDictionary<ushort, Action<string, byte[]>>();
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        /// <summary>The room this client is currently in, or null.</summary>
        public RoomInfo? CurrentRoom
        {
            get
            {
                lock (_gate)
                    return _code == null ? null : new RoomInfo(_code, _ownId, new List<string>(_members));
            }
        }

        /// <summary>Raised when another player joins the current room (arg: their player id).</summary>
        public event Action<string>? PlayerJoined;

        /// <summary>Raised when a player leaves the current room (arg: their player id).</summary>
        public event Action<string>? PlayerLeft;

        /// <summary>
        /// Catch-all for broadcasts whose message-type id has no <see cref="On{T}"/> handler registered (args: sender player id,
        /// message-type id, raw body). Prefer <see cref="On{T}"/> for typed dispatch; this fires for unregistered types
        /// (including the default type <c>0</c> used by the untyped <see cref="BroadcastAsync{T}(T)"/> overload).
        /// </summary>
        public event Action<string, ushort, byte[]>? MessageReceived;

        /// <summary>Raised when the current room closes.</summary>
        public event Action? Closed;

        internal RoomsClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _subscriptions.Add(_client.OnRaw(Channels.Rooms, (ushort)RoomEvt.PlayerJoined, body => OnPlayerEvent(true, body)));
            _subscriptions.Add(_client.OnRaw(Channels.Rooms, (ushort)RoomEvt.PlayerLeft, body => OnPlayerEvent(false, body)));
            _subscriptions.Add(_client.OnRaw(Channels.Rooms, (ushort)RoomEvt.Message, OnMessageEvent));
            _subscriptions.Add(_client.OnRaw(Channels.Rooms, (ushort)RoomEvt.Closed, OnClosedEvent));
        }

        /// <summary>Creates a new room and joins it; returns the room (with its join code).</summary>
        public async Task<RoomInfo> CreateAsync(RoomOptions? options = null)
            => ApplyRoom(await RequestAsync((ushort)RoomOp.Create, RoomWire.EncodeCreate(options?.MaxPlayers ?? 0)).ConfigureAwait(false));

        /// <summary>Joins an existing room by code; throws <see cref="RoomException"/> if it's missing or full.</summary>
        public async Task<RoomInfo> JoinAsync(string code)
            => ApplyRoom(await RequestAsync((ushort)RoomOp.Join, RoomWire.EncodeJoin(code)).ConfigureAwait(false));

        /// <summary>
        /// Leaves the current room. Tolerant of a dropped connection — if the client is already disconnected the server
        /// has removed us anyway (via <c>PeerDisconnected</c>), so local state is cleared without throwing.
        /// </summary>
        public async Task LeaveAsync()
        {
            try { await RequestAsync((ushort)RoomOp.Leave, Array.Empty<byte>()).ConfigureAwait(false); }
            catch { /* already disconnected — the server auto-removes us; just clear local state */ }
            lock (_gate) { _code = null; _members.Clear(); }
        }

        /// <summary>Broadcasts raw bytes to the other members under a message-type id, routed on the far side to <see cref="On{T}"/> or <see cref="MessageReceived"/>.</summary>
        public Task BroadcastAsync(ushort messageType, byte[] body)
            => _client.PostRawAsync(Channels.Rooms, (ushort)RoomOp.Broadcast, RoomWire.FrameBroadcast(messageType, body ?? Array.Empty<byte>()));

        /// <summary>Broadcasts raw bytes under the default message-type id (<c>0</c>).</summary>
        public Task BroadcastAsync(byte[] payload) => BroadcastAsync((ushort)0, payload);

        /// <summary>Serializes and broadcasts a message under the default message-type id (<c>0</c>). Receive with <c>On&lt;T&gt;(0, …)</c> or the raw <see cref="MessageReceived"/>.</summary>
        public Task BroadcastAsync<T>(T message) => BroadcastAsync((ushort)0, SetNetSerializer.Serialize(message));

        /// <summary>Serializes and broadcasts a message under a message-type id — receive it typed with <c>On&lt;T&gt;(messageType, …)</c>.</summary>
        public Task BroadcastAsync<T>(ushort messageType, T message) => BroadcastAsync(messageType, SetNetSerializer.Serialize(message));

        /// <summary>
        /// Registers a typed handler for one broadcast message-type id: the body is deserialized to <typeparamref name="T"/>
        /// via <see cref="SetNetSerializer"/> and your callback is invoked with (senderPlayerId, message). Overwrites any
        /// handler for the same id.
        /// </summary>
        public void On<T>(ushort messageType, Action<string, T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _typed[messageType] = (sender, body) => handler(sender, SetNetSerializer.Deserialize<T>(body));
        }

        /// <summary>Removes the typed handler for a broadcast message-type id (it then falls through to <see cref="MessageReceived"/>).</summary>
        public void Off(ushort messageType) => _typed.TryRemove(messageType, out _);

        /// <summary>Sends a room command and maps protocol failures back to the public <see cref="RoomException"/>.</summary>
        private async Task<byte[]> RequestAsync(ushort op, byte[] body)
        {
            try { return await _client.RequestRawAsync(Channels.Rooms, op, body).ConfigureAwait(false); }
            catch (ProtocolException ex) { throw new RoomException(ex.Message); }
            catch (TimeoutException) { throw new RoomException("Room command timed out."); }
        }

        private RoomInfo ApplyRoom(byte[] replyBody)
        {
            var (code, ownId, members) = RoomWire.DecodeReply(replyBody);
            lock (_gate)
            {
                _code = code;
                _ownId = ownId;
                _members.Clear();
                foreach (var m in members) _members.Add(m);
                return new RoomInfo(_code, _ownId, new List<string>(_members));
            }
        }

        private void OnPlayerEvent(bool joined, byte[] body)
        {
            var (code, playerId) = RoomWire.DecodePlayer(body);
            lock (_gate) { if (_code == null || _code != code) return; }   // not my room
            if (joined)
            {
                lock (_gate) _members.Add(playerId);
                PlayerJoined?.Invoke(playerId);
            }
            else
            {
                lock (_gate) _members.Remove(playerId);
                PlayerLeft?.Invoke(playerId);
            }
        }

        private void OnMessageEvent(byte[] body)
        {
            var (code, sender, messageType, payload) = RoomWire.DecodeMessage(body);
            lock (_gate) { if (_code == null || _code != code) return; }   // not my room
            if (_typed.TryGetValue(messageType, out var handler)) handler(sender, payload);   // typed handler consumes it
            else MessageReceived?.Invoke(sender, messageType, payload);                       // otherwise the catch-all
        }

        private void OnClosedEvent(byte[] body)
        {
            var code = RoomWire.DecodeCode(body);
            lock (_gate) { if (_code == null || _code != code) return; _code = null; _members.Clear(); }
            Closed?.Invoke();
        }
    }

    /// <summary>Attaches rooms to a <see cref="BaseClient"/> by composition — no base class.</summary>
    public static class RoomsClientExtensions
    {
        /// <summary>Enables rooms on a client and returns the driver (create/join/leave/broadcast + events).</summary>
        public static RoomsClient UseRooms(this BaseClient client) => new RoomsClient(client);
    }
}
