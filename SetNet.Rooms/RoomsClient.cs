using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;

namespace SetNet.Rooms
{
    /// <summary>
    /// Client-side rooms driver, attached by <see cref="RoomsClientExtensions.UseRooms"/>. Create/join a room by
    /// code, broadcast to it, and receive player-joined/left and message events — all by composition, alongside
    /// your regular messages.
    /// </summary>
    public sealed class RoomsClient
    {
        private readonly BaseClient _client;
        private readonly object _gate = new object();
        private string? _code;
        private string _ownId = "";
        private readonly HashSet<string> _members = new HashSet<string>();

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

        /// <summary>Raised on a broadcast from another member (args: sender player id, raw payload — deserialize with your serializer).</summary>
        public event Action<string, byte[]>? MessageReceived;

        /// <summary>Raised when the current room closes.</summary>
        public event Action? Closed;

        internal RoomsClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            RoomRegistry.RegisterClient(this);
        }

        /// <summary>Creates a new room and joins it; returns the room (with its join code).</summary>
        public async Task<RoomInfo> CreateAsync(RoomOptions? options = null)
            => ApplyRoom(await SendAsync(RoomOp.Create, "", options?.MaxPlayers ?? 0, Array.Empty<byte>()).ConfigureAwait(false));

        /// <summary>Joins an existing room by code; throws <see cref="RoomException"/> if it's missing or full.</summary>
        public async Task<RoomInfo> JoinAsync(string code)
            => ApplyRoom(await SendAsync(RoomOp.Join, code, 0, Array.Empty<byte>()).ConfigureAwait(false));

        /// <summary>Leaves the current room.</summary>
        public async Task LeaveAsync()
        {
            await SendAsync(RoomOp.Leave, "", 0, Array.Empty<byte>()).ConfigureAwait(false);
            lock (_gate) { _code = null; _members.Clear(); }
        }

        /// <summary>Broadcasts raw bytes to the other members of the current room (fire-and-forget).</summary>
        public Task BroadcastAsync(byte[] payload)
        {
            var command = new RoomCommand(0, RoomOp.Broadcast, CurrentRoom?.Code ?? "", 0, payload);
            return _client.SendAsync(RoomTypes.Command, command.Encode(), DeliveryMethod.Reliable);
        }

        /// <summary>Serializes and broadcasts a message to the other members of the current room.</summary>
        public Task BroadcastAsync<T>(T message) => BroadcastAsync(SetNetSerializer.Serialize(message));

        private RoomInfo ApplyRoom(RoomReply reply)
        {
            if (!reply.Success) throw new RoomException(reply.Error);
            lock (_gate)
            {
                _code = reply.Code;
                _ownId = reply.OwnPlayerId;
                _members.Clear();
                foreach (var m in reply.Members) _members.Add(m);
                return new RoomInfo(_code, _ownId, new List<string>(_members));
            }
        }

        private async Task<RoomReply> SendAsync(RoomOp op, string code, int maxPlayers, byte[] payload)
        {
            var correlationId = RoomRegistry.NextId();
            var tcs = new TaskCompletionSource<RoomReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            RoomRegistry.Register(correlationId, tcs);
            try
            {
                var command = new RoomCommand(correlationId, op, code, maxPlayers, payload);
                await _client.SendAsync(RoomTypes.Command, command.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try { return await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new RoomException("Room command timed out."); }
                }
            }
            finally { RoomRegistry.Remove(correlationId); }
        }

        internal void OnEvent(RoomEvent evt)
        {
            lock (_gate)
            {
                if (_code == null || _code != evt.Code) return;   // not my room
            }
            switch (evt.Type)
            {
                case RoomEventType.PlayerJoined:
                    lock (_gate) _members.Add(evt.PlayerId);
                    PlayerJoined?.Invoke(evt.PlayerId);
                    break;
                case RoomEventType.PlayerLeft:
                    lock (_gate) _members.Remove(evt.PlayerId);
                    PlayerLeft?.Invoke(evt.PlayerId);
                    break;
                case RoomEventType.Message:
                    MessageReceived?.Invoke(evt.PlayerId, evt.Payload);
                    break;
                case RoomEventType.Closed:
                    lock (_gate) { _code = null; _members.Clear(); }
                    Closed?.Invoke();
                    break;
            }
        }
    }

    /// <summary>Attaches rooms to a <see cref="BaseClient"/> by composition — no base class.</summary>
    public static class RoomsClientExtensions
    {
        /// <summary>Enables rooms on a client and returns the driver (create/join/leave/broadcast + events).</summary>
        public static RoomsClient UseRooms(this BaseClient client) => new RoomsClient(client);
    }

    /// <summary>Auto-discovered client handler for room command replies (correlated).</summary>
    [MessageHandler(RoomTypes.Reply)]
    public sealed class RoomReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            var reply = RoomReply.Decode(data);
            RoomRegistry.Complete(reply.CorrelationId, reply);
            return Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for room push events; routes to the matching <see cref="RoomsClient"/>.</summary>
    [MessageHandler(RoomTypes.Event)]
    public sealed class RoomEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            RoomRegistry.DispatchEvent(RoomEvent.Decode(data));
            return Task.CompletedTask;
        }
    }
}
