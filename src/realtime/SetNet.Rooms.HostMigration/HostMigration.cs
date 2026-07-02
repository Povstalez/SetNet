using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;
using SetNet.Rooms;

namespace SetNet.Rooms.HostMigration
{
    /// <summary>Push events (server → client) within the HostMigration protocol channel.</summary>
    internal enum HostMigrationEvt : ushort { HostChanged = 10 }

    /// <summary>
    /// Host migration on top of [SetNet.Rooms]: designates the room's creator (first member) as host and, when the host
    /// leaves or disconnects, promotes the next remaining member and notifies the room. Uses the public
    /// <c>server.RoomHooks()</c> room-lifecycle events; no relay, node-local (matching Rooms). Rides the unified
    /// protocol on the <see cref="Channels.HostMigration"/> channel.
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
                try { _ = m.PublishRawAsync(Channels.HostMigration, (ushort)HostMigrationEvt.HostChanged, wire); } catch { /* dropping */ }
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
        private readonly IDisposable _subscription;

        /// <summary>Raised when a room's host changes (args: room code, new host player id).</summary>
        public event Action<string, string>? HostChanged;

        internal HostMigrationClient(BaseClient client)
        {
            _subscription = client.OnRaw(Channels.HostMigration, (ushort)HostMigrationEvt.HostChanged, body =>
            {
                var (code, hostId) = HostMigrationServer.Decode(body);
                HostChanged?.Invoke(code, hostId);
            });
        }
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
            return new HostMigrationClient(client);
        }
    }

    /// <summary>One-time bootstrap so the host-migration layer is loaded. Call at startup.</summary>
    public static class HostMigrationRuntime
    {
        /// <summary>Ensures the host-migration layer is discoverable.</summary>
        public static void Enable() { _ = typeof(HostMigrationClient); }
    }
}
