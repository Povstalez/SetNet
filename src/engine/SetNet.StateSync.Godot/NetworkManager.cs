using System;
using System.Collections.Generic;
using Godot;
using SetNet.Core;
using SetNet.Godot;
using SetNet.StateSync;

namespace SetNet.StateSync.Godot
{
    /// <summary>
    /// The Godot driver for SetNet state replication. Give it your networked scenes (each a <c>PackedScene</c> whose root
    /// is a <see cref="NetworkObject"/>), then start it as a server or a client over a SetNet <c>BaseServer</c>/<c>BaseClient</c>
    /// you created (any transport). It registers each scene's schema, spawns/despawns nodes as entities enter/leave the
    /// client's view, pushes server component state every frame, and applies interpolated state to client objects —
    /// marshalling spawn/despawn onto Godot's main thread via <see cref="GodotMainThreadDispatcher"/>.
    /// </summary>
    [GlobalClass]
    public partial class NetworkManager : Node
    {
        /// <summary>One scene per archetype; the same list must be assigned on the server and every client.</summary>
        [Export] public global::Godot.Collections.Array<PackedScene> RegisteredScenes { get; set; } = new global::Godot.Collections.Array<PackedScene>();

        private readonly Dictionary<ushort, PackedScene> _byArchetype = new Dictionary<ushort, PackedScene>();
        private readonly Dictionary<uint, NetworkObject> _clientObjects = new Dictionary<uint, NetworkObject>();
        private readonly List<NetworkObject> _serverObjects = new List<NetworkObject>();

        private ServerReplication? _server;
        private ClientReplication? _client;

        /// <summary>True once started as a server.</summary>
        public bool IsServer => _server != null;

        /// <summary>True once started as a client.</summary>
        public bool IsClient => _client != null;

        /// <summary>The client driver (input, entities), or null on a server.</summary>
        public ClientReplication? Client => _client;

        /// <summary>The server world (spawn/despawn/observers/input), or null on a client.</summary>
        public ServerReplication? Server => _server;

        /// <summary>Starts replication as the authoritative server over an already-constructed SetNet server.</summary>
        public void StartServer(BaseServer server, StateSyncOptions? options = null)
        {
            StateSyncRuntime.Enable();
            RegisterSchemas();
            _server = server.UseStateSync(options);
        }

        /// <summary>Starts replication as a client over an already-constructed SetNet client.</summary>
        public void StartClient(BaseClient client, StateSyncOptions? options = null)
        {
            StateSyncRuntime.Enable();
            RegisterSchemas();
            _client = client.UseStateSync(options);
            _client.EntitySpawned += OnClientSpawn;
            _client.EntityDespawned += OnClientDespawn;
        }

        /// <summary>Server: creates a networked instance of an archetype at a pose, optionally owned by a peer.</summary>
        public NetworkObject ServerSpawn(ushort archetype, Vector3 position, Quaternion rotation, Guid owner = default)
        {
            if (_server == null) throw new InvalidOperationException("StartServer must be called first.");
            var scene = _byArchetype[archetype];
            var go = scene.Instantiate<NetworkObject>();
            go.Position = position;
            go.Quaternion = rotation;
            AddChild(go);
            var entity = _server.Spawn(archetype, owner);
            go.BindServer(entity);
            go.ServerSerialize();
            _serverObjects.Add(go);
            return go;
        }

        /// <summary>Server: removes a networked instance (despawns it everywhere and frees the node).</summary>
        public void ServerDespawn(NetworkObject obj)
        {
            if (_server == null || obj.Entity == null) return;
            _server.Despawn(obj.Entity);
            _serverObjects.Remove(obj);
            obj.QueueFree();
        }

        private void RegisterSchemas()
        {
            if (_byArchetype.Count > 0) return;
            foreach (var scene in RegisteredScenes)
            {
                if (scene == null) continue;
                var probe = scene.Instantiate<NetworkObject>();
                ReplicaRegistry.Register(probe.BuildSchema());
                _byArchetype[probe.Archetype] = scene;
                probe.QueueFree();
            }
        }

        /// <inheritdoc/>
        public override void _Process(double delta)
        {
            MainThreadDrain();

            if (_server != null)
                for (var i = 0; i < _serverObjects.Count; i++) _serverObjects[i].ServerSerialize();

            if (_client != null)
            {
                _client.Update();
                foreach (var kv in _clientObjects) kv.Value.ClientDeserialize();
            }
        }

        private static void MainThreadDrain() => GodotMainThreadDispatcher.Shared.Drain();

        // Fired on the network receive thread → marshal onto the main thread before touching the scene tree.
        private void OnClientSpawn(NetworkEntityView view) => GodotMainThreadDispatcher.Shared.Post(() =>
        {
            if (_clientObjects.ContainsKey(view.NetId)) return;
            if (!_byArchetype.TryGetValue(view.ArchetypeId, out var scene))
            {
                GD.PushWarning($"[SetNet] No scene registered for archetype {view.ArchetypeId}; entity {view.NetId} not spawned.");
                return;
            }
            var go = scene.Instantiate<NetworkObject>();
            AddChild(go);
            go.BindClient(view);
            _clientObjects[view.NetId] = go;
        });

        private void OnClientDespawn(NetworkEntityView view) => GodotMainThreadDispatcher.Shared.Post(() =>
        {
            if (_clientObjects.TryGetValue(view.NetId, out var go))
            {
                _clientObjects.Remove(view.NetId);
                if (IsInstanceValid(go)) go.QueueFree();
            }
        });
    }
}
