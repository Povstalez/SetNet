using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Rooms;

namespace SetNet.Rooms.HostMigration
{
    /// <summary>Reserved wire type for host-changed notifications (below the party range). Don't reuse.</summary>
    public static class HostMigrationTypes
    {
        /// <summary>Server → client: the host of a room changed.</summary>
        public const ushort HostChanged = ushort.MaxValue - 27;   // 65508
    }

    /// <summary>
    /// Host migration on top of [SetNet.Rooms]: designates the room's creator (first member) as host and, when the host
    /// leaves or disconnects, promotes the next remaining member and notifies the room. Uses the public
    /// <c>server.RoomHooks()</c> room-lifecycle events; no relay, node-local (matching Rooms).
    /// </summary>
    public sealed class HostMigrationServer
    {
        private readonly ConcurrentDictionary<string, Guid> _host = new ConcurrentDictionary<string, Guid>();
        private readonly ConcurrentDictionary<string, List<BasePeer>> _members = new ConcurrentDictionary<string, List<BasePeer>>();
        private readonly object _gate = new object();

        internal HostMigrationServer(BaseServer server)
        {
            var hooks = server.RoomHooks();
            hooks.PeerJoinedRoom += OnJoined;
            hooks.PeerLeftRoom += OnLeft;
        }

        private void OnJoined(string code, BasePeer peer)
        {
            lock (_gate)
            {
                var list = _members.GetOrAdd(code, _ => new List<BasePeer>());
                if (!list.Contains(peer)) list.Add(peer);
                _host.TryAdd(code, peer.CurrentPeerInfo.Id);   // first joiner becomes host
            }
        }

        private void OnLeft(string code, BasePeer peer, IReadOnlyList<BasePeer> remaining)
        {
            bool hostLeft; BasePeer? newHost = null;
            lock (_gate)
            {
                if (_members.TryGetValue(code, out var list)) list.Remove(peer);

                hostLeft = _host.TryGetValue(code, out var host) && host == peer.CurrentPeerInfo.Id;
                if (remaining.Count == 0) { _host.TryRemove(code, out _); _members.TryRemove(code, out _); return; }
                if (hostLeft) { newHost = remaining[0]; _host[code] = newHost.CurrentPeerInfo.Id; }
            }

            if (!hostLeft || newHost == null) return;
            var wire = Encode(code, newHost.CurrentPeerInfo.Id.ToString("N"));
            foreach (var m in remaining)
            {
                try { _ = m.SendAsync(HostMigrationTypes.HostChanged, wire, DeliveryMethod.Reliable); } catch { /* dropping */ }
            }
        }

        internal static byte[] Encode(string code, string hostId)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) { w.Write(code ?? ""); w.Write(hostId ?? ""); }
            return ms.ToArray();
        }

        internal static (string code, string hostId) Decode(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return (r.ReadString(), r.ReadString());
        }
    }

    /// <summary>Client-side host-migration driver.</summary>
    public sealed class HostMigrationClient
    {
        /// <summary>Raised when a room's host changes (args: room code, new host player id).</summary>
        public event Action<string, string>? HostChanged;

        internal HostMigrationClient() => HostMigrationRegistry.RegisterClient(this);

        internal void OnHostChanged(string code, string hostId) => HostChanged?.Invoke(code, hostId);
    }

    internal static class HostMigrationRegistry
    {
        private static readonly ConcurrentDictionary<HostMigrationClient, byte> Clients = new ConcurrentDictionary<HostMigrationClient, byte>();
        public static void RegisterClient(HostMigrationClient c) => Clients[c] = 0;
        public static void ForEachClient(Action<HostMigrationClient> action) { foreach (var c in Clients.Keys) action(c); }
    }

    /// <summary>Attaches host migration by composition — no base class.</summary>
    public static class HostMigrationExtensions
    {
        /// <summary>Enables server-side host migration (requires <c>server.UseRooms(...)</c>).</summary>
        public static HostMigrationServer UseHostMigration(this BaseServer server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return new HostMigrationServer(server);
        }

        /// <summary>Enables the client-side host-migration driver (HostChanged event).</summary>
        public static HostMigrationClient UseHostMigration(this BaseClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return new HostMigrationClient();
        }
    }

    /// <summary>Auto-discovered client handler for host-changed notifications.</summary>
    [MessageHandler(HostMigrationTypes.HostChanged)]
    public sealed class HostChangedHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            var (code, hostId) = HostMigrationServer.Decode(data);
            HostMigrationRegistry.ForEachClient(c => c.OnHostChanged(code, hostId));
            return Task.CompletedTask;
        }
    }

    /// <summary>One-time bootstrap so the host-migration handler is discovered. Call at startup.</summary>
    public static class HostMigrationRuntime
    {
        /// <summary>Ensures the host-migration layer is discoverable.</summary>
        public static void Enable() { _ = HostMigrationTypes.HostChanged; }
    }
}
