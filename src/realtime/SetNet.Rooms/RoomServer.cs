using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.Rooms
{
    /// <summary>
    /// Public server-side room lifecycle events, retrieved via <see cref="RoomServer.RoomHooks"/>. Companion packages
    /// (e.g. host migration) subscribe to react when a peer joins or leaves a specific room on this node.
    /// </summary>
    public sealed class RoomServerHooks
    {
        /// <summary>Raised when a peer joins a room (args: room code, peer).</summary>
        public event Action<string, BasePeer>? PeerJoinedRoom;

        /// <summary>Raised when a peer leaves a room (args: room code, the peer that left, the members that remain).</summary>
        public event Action<string, BasePeer, IReadOnlyList<BasePeer>>? PeerLeftRoom;

        internal void RaiseJoined(string code, BasePeer peer) { try { PeerJoinedRoom?.Invoke(code, peer); } catch { /* isolate */ } }
        internal void RaiseLeft(string code, BasePeer peer, IReadOnlyList<BasePeer> remaining) { try { PeerLeftRoom?.Invoke(code, peer, remaining); } catch { /* isolate */ } }
    }

    /// <summary>Per-server rooms state: the room store and a peer→room index (a peer is in at most one room in v1).</summary>
    internal sealed class RoomServerState
    {
        public IRoomStore Store = null!;
        public RoomServerHooks? Hooks;
        public readonly ConcurrentDictionary<Guid, Room> MemberRoom = new ConcurrentDictionary<Guid, Room>();

        /// <summary>Live code→room index of the rooms that have members on this node (for O(1) server-side membership queries).</summary>
        public readonly ConcurrentDictionary<string, Room> Rooms = new ConcurrentDictionary<string, Room>();

        public static string PlayerId(BasePeer peer) => peer.CurrentPeerInfo.Id.ToString("N");

        public void AddMember(Room room, BasePeer peer)
        {
            room.Members[peer.CurrentPeerInfo.Id] = peer;
            MemberRoom[peer.CurrentPeerInfo.Id] = room;
            Rooms[room.Code] = room;
            Hooks?.RaiseJoined(room.Code, peer);
        }

        /// <summary>Removes the peer from its room (if any), notifies the remaining members, and drops the room if empty.</summary>
        public async Task LeaveAsync(BasePeer peer)
        {
            if (!MemberRoom.TryRemove(peer.CurrentPeerInfo.Id, out var room)) return;
            room.Members.TryRemove(peer.CurrentPeerInfo.Id, out _);
            var remaining = new List<BasePeer>(room.Members.Values);
            await NotifyOthersAsync(room, peer, (ushort)RoomEvt.PlayerLeft,
                RoomWire.EncodePlayer(room.Code, PlayerId(peer))).ConfigureAwait(false);
            Hooks?.RaiseLeft(room.Code, peer, remaining);
            if (room.Count == 0)
            {
                await Store.RemoveAsync(room).ConfigureAwait(false);
                Rooms.TryRemove(room.Code, out _);
            }
        }

        /// <summary>Pushes an event (op + body) to every member except <paramref name="except"/> (best-effort).</summary>
        public async Task NotifyOthersAsync(Room room, BasePeer except, ushort evtOp, byte[] body)
        {
            foreach (var member in room.Members)
            {
                if (member.Key == except.CurrentPeerInfo.Id) continue;
                try { await member.Value.PublishRawAsync(Channels.Rooms, evtOp, body).ConfigureAwait(false); }
                catch { /* member dropping; skip */ }
            }
        }

        public List<string> MemberIds(Room room)
        {
            var ids = new List<string>(room.Count);
            foreach (var id in room.Members.Keys) ids.Add(id.ToString("N"));
            return ids;
        }
    }

    /// <summary>
    /// Server-side rooms entry point. Call <see cref="UseRooms"/> once after constructing your server; it registers
    /// the room state and auto-removes a peer from its room on disconnect (via the core
    /// <see cref="BaseServer.PeerDisconnected"/> event). Room traffic rides the unified protocol on the
    /// <see cref="Channels.Rooms"/> channel — no per-module wire types. No base class needed.
    /// </summary>
    public static class RoomServer
    {
        private static readonly ConcurrentDictionary<BaseServer, RoomServerState> _servers
            = new ConcurrentDictionary<BaseServer, RoomServerState>();
        private static readonly ConcurrentDictionary<BaseServer, RoomServerHooks> _hooks
            = new ConcurrentDictionary<BaseServer, RoomServerHooks>();

        /// <summary>Enables rooms on a server. Supply a custom <see cref="IRoomStore"/> or use the default in-memory one.</summary>
        public static void UseRooms(this BaseServer server, IRoomStore? store = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            var state = new RoomServerState { Store = store ?? new MemoryRoomStore(), Hooks = RoomHooks(server) };
            _servers[server] = state;
            server.PeerDisconnected += peer => _ = SafeLeave(state, peer);
            server.RegisterModule(new RoomServerRegistration(server));
        }

        /// <summary>Gets the server-side room lifecycle events for this server (peer joined/left a room). Used by companion packages such as host migration.</summary>
        public static RoomServerHooks RoomHooks(this BaseServer server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return _hooks.GetOrAdd(server, _ => new RoomServerHooks());
        }

        private static async Task SafeLeave(RoomServerState state, BasePeer peer)
        {
            try { await state.LeaveAsync(peer).ConfigureAwait(false); } catch { /* teardown */ }
        }

        internal static RoomServerState? Get(BaseServer? server)
            => server != null && _servers.TryGetValue(server, out var state) ? state : null;

        private sealed class RoomServerRegistration : IDisposable
        {
            private readonly BaseServer _server;

            public RoomServerRegistration(BaseServer server) => _server = server;

            public void Dispose()
            {
                _servers.TryRemove(_server, out _);
                _hooks.TryRemove(_server, out _);
            }
        }
    }

    /// <summary>
    /// Auto-discovered channel service for room commands (create/join/leave/broadcast). Replaces the former
    /// hand-framed <c>[MessageHandler]</c> classes and correlation plumbing: the unified protocol handles
    /// correlation and reply framing, so this only implements the room logic and dispatches on the op.
    /// </summary>
    [ProtocolChannel(Channels.Rooms)]
    public sealed class RoomsChannelService : IChannelService
    {
        /// <inheritdoc/>
        public async Task HandleAsync(ChannelRequest request)
        {
            var state = RoomServer.Get(request.Peer.CurrentPeerInfo.Server);
            if (state == null) throw new ProtocolException("rooms are not configured on this server");
            var peer = request.Peer;

            switch ((RoomOp)request.Op)
            {
                case RoomOp.Create:
                {
                    await state.LeaveAsync(peer).ConfigureAwait(false);   // one room per peer
                    var room = await state.Store.CreateAsync(RoomWire.DecodeCreate(request.RawBody)).ConfigureAwait(false);
                    state.AddMember(room, peer);
                    await request.ReplyRawAsync(
                        RoomWire.EncodeReply(room.Code, RoomServerState.PlayerId(peer), state.MemberIds(room))).ConfigureAwait(false);
                    break;
                }
                case RoomOp.Join:
                {
                    var room = await state.Store.GetAsync(RoomWire.DecodeJoin(request.RawBody)).ConfigureAwait(false);
                    if (room == null) throw new ProtocolException("room not found");
                    if (room.IsFull) throw new ProtocolException("room full");
                    await state.LeaveAsync(peer).ConfigureAwait(false);
                    state.AddMember(room, peer);
                    await state.NotifyOthersAsync(room, peer, (ushort)RoomEvt.PlayerJoined,
                        RoomWire.EncodePlayer(room.Code, RoomServerState.PlayerId(peer))).ConfigureAwait(false);
                    await request.ReplyRawAsync(
                        RoomWire.EncodeReply(room.Code, RoomServerState.PlayerId(peer), state.MemberIds(room))).ConfigureAwait(false);
                    break;
                }
                case RoomOp.Leave:
                {
                    await state.LeaveAsync(peer).ConfigureAwait(false);
                    if (request.ExpectsReply) await request.ReplyRawAsync(Array.Empty<byte>()).ConfigureAwait(false);
                    break;
                }
                case RoomOp.Broadcast:
                {
                    if (state.MemberRoom.TryGetValue(peer.CurrentPeerInfo.Id, out var room))
                    {
                        var (messageType, payload) = RoomWire.UnframeBroadcast(request.RawBody);
                        await state.NotifyOthersAsync(room, peer, (ushort)RoomEvt.Message,
                            RoomWire.EncodeMessage(room.Code, RoomServerState.PlayerId(peer), messageType, payload)).ConfigureAwait(false);
                    }
                    break;
                }
            }
        }
    }
}
