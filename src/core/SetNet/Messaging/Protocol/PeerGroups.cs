using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Core;

namespace SetNet.Protocol
{
    /// <summary>
    /// A server-side grouping of connected peers — a room, a party, a guild, a chat channel, and so on. A module
    /// implements this over its own membership state (two small methods) and, in return, gets the whole family of
    /// query and room-scoped-broadcast extension methods in <see cref="PeerGroupsExtensions"/> for free — so
    /// "broadcast to everyone in the group / everyone except the sender" doesn't have to be hand-rolled per module.
    /// </summary>
    /// <remarks>Node-local: members are the live connections on this server (matching how Rooms/Party track membership).</remarks>
    public interface IPeerGroups
    {
        /// <summary>The live members of the group with the given key (empty if the group is unknown here).</summary>
        IReadOnlyList<BasePeer> MembersOf(string groupKey);

        /// <summary>The group key the peer currently belongs to for this grouping, or null if none.</summary>
        string? GroupKeyOf(BasePeer peer);
    }

    /// <summary>
    /// Query and broadcast helpers over any <see cref="IPeerGroups"/> — the shared implementation behind each module's
    /// friendly aliases (e.g. <c>server.OthersInRoomOf(peer)</c>, <c>server.BroadcastToRoomOfAsync(...)</c>).
    /// Broadcasts ride the unified protocol via <see cref="ProtocolPeerExtensions"/>.
    /// </summary>
    public static class PeerGroupsExtensions
    {
        /// <summary>The members of the peer's group (including the peer); empty if it isn't in one.</summary>
        public static IReadOnlyList<BasePeer> MembersOf(this IPeerGroups groups, BasePeer peer)
        {
            if (groups == null) throw new ArgumentNullException(nameof(groups));
            if (peer == null) return Array.Empty<BasePeer>();
            var key = groups.GroupKeyOf(peer);
            return key == null ? Array.Empty<BasePeer>() : groups.MembersOf(key);
        }

        /// <summary>The members of the peer's group excluding the peer itself (i.e. "everyone else in my room/party").</summary>
        public static IReadOnlyList<BasePeer> OthersOf(this IPeerGroups groups, BasePeer peer)
        {
            var all = MembersOf(groups, peer);
            if (all.Count == 0) return all;
            var id = peer.CurrentPeerInfo.Id;
            var others = new List<BasePeer>(all.Count);
            foreach (var m in all) if (m.CurrentPeerInfo.Id != id) others.Add(m);
            return others;
        }

        /// <summary>Pushes a raw event to every member of the group.</summary>
        public static Task BroadcastRawAsync(this IPeerGroups groups, string groupKey, ushort channel, ushort op, byte[]? body = null)
            => groups.MembersOf(groupKey).PublishRawAsync(channel, op, body);

        /// <summary>Serializes and pushes an event to every member of the group.</summary>
        public static Task BroadcastAsync<T>(this IPeerGroups groups, string groupKey, ushort channel, ushort op, T message)
            => groups.MembersOf(groupKey).PublishAsync(channel, op, message);

        /// <summary>Pushes an event to the peer's group — to everyone else by default, or including the peer when <paramref name="includeSelf"/> is true.</summary>
        public static Task BroadcastToGroupOfAsync<T>(this IPeerGroups groups, BasePeer peer, ushort channel, ushort op, T message, bool includeSelf = false)
            => (includeSelf ? MembersOf(groups, peer) : OthersOf(groups, peer)).PublishAsync(channel, op, message);

        /// <summary>Raw counterpart of <see cref="BroadcastToGroupOfAsync{T}"/>.</summary>
        public static Task BroadcastToGroupOfRawAsync(this IPeerGroups groups, BasePeer peer, ushort channel, ushort op, byte[]? body = null, bool includeSelf = false)
            => (includeSelf ? MembersOf(groups, peer) : OthersOf(groups, peer)).PublishRawAsync(channel, op, body);

        /// <summary>Pushes an event to every member of the group except one peer.</summary>
        public static Task BroadcastExceptAsync<T>(this IPeerGroups groups, string groupKey, BasePeer except, ushort channel, ushort op, T message)
        {
            var exceptId = except?.CurrentPeerInfo.Id;
            var targets = groups.MembersOf(groupKey).Where(m => m.CurrentPeerInfo.Id != exceptId).ToList();
            return targets.PublishAsync(channel, op, message);
        }
    }
}
