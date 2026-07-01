using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Rooms
{
    /// <summary>Per-server rooms state: the room store and a peer→room index (a peer is in at most one room in v1).</summary>
    internal sealed class RoomServerState
    {
        public IRoomStore Store = null!;
        public readonly ConcurrentDictionary<Guid, Room> MemberRoom = new ConcurrentDictionary<Guid, Room>();

        public static string PlayerId(BasePeer peer) => peer.CurrentPeerInfo.Id.ToString("N");

        public void AddMember(Room room, BasePeer peer)
        {
            room.Members[peer.CurrentPeerInfo.Id] = peer;
            MemberRoom[peer.CurrentPeerInfo.Id] = room;
        }

        /// <summary>Removes the peer from its room (if any), notifies the remaining members, and drops the room if empty.</summary>
        public async Task LeaveAsync(BasePeer peer)
        {
            if (!MemberRoom.TryRemove(peer.CurrentPeerInfo.Id, out var room)) return;
            room.Members.TryRemove(peer.CurrentPeerInfo.Id, out _);
            await NotifyOthersAsync(room, peer, new RoomEvent(room.Code, RoomEventType.PlayerLeft, PlayerId(peer), Array.Empty<byte>())).ConfigureAwait(false);
            if (room.Count == 0) await Store.RemoveAsync(room).ConfigureAwait(false);
        }

        /// <summary>Sends an event to every member except <paramref name="except"/> (best-effort).</summary>
        public async Task NotifyOthersAsync(Room room, BasePeer except, RoomEvent evt)
        {
            var bytes = evt.Encode();
            foreach (var member in room.Members)
            {
                if (member.Key == except.CurrentPeerInfo.Id) continue;
                try { await member.Value.SendAsync(RoomTypes.Event, bytes, DeliveryMethod.Reliable).ConfigureAwait(false); }
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
    /// Server-side rooms entry point. Call <see cref="UseRooms"/> once after constructing your server; it wires the
    /// auto-discovered command handler and auto-removes a peer from its room on disconnect (via the core
    /// <see cref="BaseServer.PeerDisconnected"/> event). No base class needed.
    /// </summary>
    public static class RoomServer
    {
        private static readonly ConcurrentDictionary<BaseServer, RoomServerState> _servers
            = new ConcurrentDictionary<BaseServer, RoomServerState>();

        /// <summary>Enables rooms on a server. Supply a custom <see cref="IRoomStore"/> or use the default in-memory one.</summary>
        public static void UseRooms(this BaseServer server, IRoomStore? store = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            var state = new RoomServerState { Store = store ?? new MemoryRoomStore() };
            _servers[server] = state;
            server.PeerDisconnected += peer => _ = SafeLeave(state, peer);
        }

        private static async Task SafeLeave(RoomServerState state, BasePeer peer)
        {
            try { await state.LeaveAsync(peer).ConfigureAwait(false); } catch { /* teardown */ }
        }

        internal static RoomServerState? Get(BaseServer? server)
            => server != null && _servers.TryGetValue(server, out var state) ? state : null;
    }

    /// <summary>Auto-discovered handler for room commands (create/join/leave/broadcast). Serializer-agnostic (byte[]).</summary>
    [MessageHandler(RoomTypes.Command)]
    public sealed class RoomServerHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public async Task HandleAsync(BasePeer peer, byte[] data)
        {
            var cmd = RoomCommand.Decode(data);
            var state = RoomServer.Get(peer.CurrentPeerInfo.Server);
            if (state == null)
            {
                await ReplyAsync(peer, RoomReply.Fail(cmd.CorrelationId, "rooms are not configured on this server")).ConfigureAwait(false);
                return;
            }

            switch (cmd.Op)
            {
                case RoomOp.Create:
                {
                    await state.LeaveAsync(peer).ConfigureAwait(false);   // one room per peer
                    var room = await state.Store.CreateAsync(cmd.MaxPlayers).ConfigureAwait(false);
                    state.AddMember(room, peer);
                    await ReplyAsync(peer, RoomReply.Ok(cmd.CorrelationId, room.Code, RoomServerState.PlayerId(peer), state.MemberIds(room))).ConfigureAwait(false);
                    break;
                }
                case RoomOp.Join:
                {
                    var room = await state.Store.GetAsync(cmd.Code).ConfigureAwait(false);
                    if (room == null) { await ReplyAsync(peer, RoomReply.Fail(cmd.CorrelationId, "room not found")).ConfigureAwait(false); break; }
                    if (room.IsFull) { await ReplyAsync(peer, RoomReply.Fail(cmd.CorrelationId, "room full")).ConfigureAwait(false); break; }
                    await state.LeaveAsync(peer).ConfigureAwait(false);
                    state.AddMember(room, peer);
                    await state.NotifyOthersAsync(room, peer,
                        new RoomEvent(room.Code, RoomEventType.PlayerJoined, RoomServerState.PlayerId(peer), Array.Empty<byte>())).ConfigureAwait(false);
                    await ReplyAsync(peer, RoomReply.Ok(cmd.CorrelationId, room.Code, RoomServerState.PlayerId(peer), state.MemberIds(room))).ConfigureAwait(false);
                    break;
                }
                case RoomOp.Leave:
                {
                    await state.LeaveAsync(peer).ConfigureAwait(false);
                    if (cmd.CorrelationId != 0)
                        await ReplyAsync(peer, RoomReply.Ok(cmd.CorrelationId, "", RoomServerState.PlayerId(peer), Array.Empty<string>())).ConfigureAwait(false);
                    break;
                }
                case RoomOp.Broadcast:
                {
                    if (state.MemberRoom.TryGetValue(peer.CurrentPeerInfo.Id, out var room))
                        await state.NotifyOthersAsync(room, peer,
                            new RoomEvent(room.Code, RoomEventType.Message, RoomServerState.PlayerId(peer), cmd.Payload)).ConfigureAwait(false);
                    break;
                }
            }
        }

        private static Task ReplyAsync(BasePeer peer, RoomReply reply)
            => peer.SendAsync(RoomTypes.Reply, reply.Encode(), DeliveryMethod.Reliable);
    }
}
