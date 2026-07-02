using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.Rooms
{
    /// <summary><see cref="IPeerGroups"/> view over a server's rooms, so the generic group query/broadcast helpers apply to rooms.</summary>
    internal sealed class RoomGroupsView : IPeerGroups
    {
        private readonly RoomServerState _state;
        public RoomGroupsView(RoomServerState state) => _state = state;

        public IReadOnlyList<BasePeer> MembersOf(string groupKey)
        {
            if (groupKey != null && _state.Rooms.TryGetValue(groupKey, out var room))
                return new List<BasePeer>(room.Members.Values);
            return Array.Empty<BasePeer>();
        }

        public string? GroupKeyOf(BasePeer peer)
            => peer != null && _state.MemberRoom.TryGetValue(peer.CurrentPeerInfo.Id, out var room) ? room.Code : null;
    }

    /// <summary>
    /// Server-side room membership queries and room-scoped broadcasts — so a dedicated server can talk to a room
    /// without hand-maintaining its own membership map. Built on the reusable <see cref="IPeerGroups"/> primitive,
    /// so the same shapes exist for other groupings (e.g. <c>server.PartyGroups()</c>).
    /// </summary>
    public static class RoomServerGroups
    {
        /// <summary>The rooms grouping as an <see cref="IPeerGroups"/> (all generic query/broadcast extensions apply); null if rooms aren't enabled.</summary>
        public static IPeerGroups? RoomGroups(this BaseServer server)
        {
            var state = RoomServer.Get(server);
            return state == null ? null : new RoomGroupsView(state);
        }

        /// <summary>The join code of the room the peer is in, or null.</summary>
        public static string? RoomCodeOf(this BaseServer server, BasePeer peer)
            => server.RoomGroups()?.GroupKeyOf(peer);

        /// <summary>All member peers of the room with the given code (empty if unknown). Node-local.</summary>
        public static IReadOnlyList<BasePeer> MembersOfRoom(this BaseServer server, string code)
            => server.RoomGroups()?.MembersOf(code) ?? Array.Empty<BasePeer>();

        /// <summary>All member peers of the peer's room, including the peer (empty if it isn't in a room).</summary>
        public static IReadOnlyList<BasePeer> MembersInRoomOf(this BaseServer server, BasePeer peer)
            => server.RoomGroups() is { } g ? g.MembersOf(peer) : Array.Empty<BasePeer>();

        /// <summary>Every other member of the peer's room (i.e. all except the peer itself).</summary>
        public static IReadOnlyList<BasePeer> OthersInRoomOf(this BaseServer server, BasePeer peer)
            => server.RoomGroups() is { } g ? g.OthersOf(peer) : Array.Empty<BasePeer>();

        /// <summary>Pushes an event to every member of the room with the given code.</summary>
        public static Task BroadcastToRoomAsync<T>(this BaseServer server, string code, ushort channel, ushort op, T message)
            => server.RoomGroups() is { } g ? g.BroadcastAsync(code, channel, op, message) : Task.CompletedTask;

        /// <summary>Pushes an event to the peer's room — to everyone else by default, or including the peer when <paramref name="includeSelf"/> is true.</summary>
        public static Task BroadcastToRoomOfAsync<T>(this BaseServer server, BasePeer peer, ushort channel, ushort op, T message, bool includeSelf = false)
            => server.RoomGroups() is { } g ? g.BroadcastToGroupOfAsync(peer, channel, op, message, includeSelf) : Task.CompletedTask;

        /// <summary>Pushes an event to every member of the room with the given code except one peer.</summary>
        public static Task BroadcastToRoomExceptAsync<T>(this BaseServer server, string code, BasePeer except, ushort channel, ushort op, T message)
            => server.RoomGroups() is { } g ? g.BroadcastExceptAsync(code, except, channel, op, message) : Task.CompletedTask;
    }
}
