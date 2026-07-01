using System;
using System.Collections.Generic;
using UnityEngine;
using SetNet.Core;
using SetNet.StateSync;
using SetNet.Unity;

namespace SetNet.StateSync.Unity
{
    /// <summary>
    /// The Unity driver for SetNet state replication. Give it your prefab list (one per archetype), then start it as a
    /// server or a client over a SetNet <see cref="BaseServer"/>/<see cref="BaseClient"/> you created (any transport).
    /// It registers each prefab's schema, spawns/despawns GameObjects as entities enter/leave the client's view, pushes
    /// server component state into entities every frame, and applies interpolated state to client objects — marshalling
    /// spawn/despawn onto Unity's main thread via <see cref="MainThreadDispatcher"/>.
    /// </summary>
    public sealed class NetworkManager : MonoBehaviour
    {
        [Tooltip("One prefab per archetype id. The same list must be assigned on the server and every client.")]
        [SerializeField] private List<NetworkObject> registeredPrefabs = new List<NetworkObject>();

        private readonly Dictionary<ushort, NetworkObject> _prefabByArchetype = new Dictionary<ushort, NetworkObject>();
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
            var prefab = _prefabByArchetype[archetype];
            var go = Instantiate(prefab, position, rotation);
            var entity = _server.Spawn(archetype, owner);
            go.BindServer(entity);
            go.ServerSerialize();   // seed initial state before the first snapshot
            _serverObjects.Add(go);
            return go;
        }

        /// <summary>Server: removes a networked instance (despawns it everywhere and destroys the GameObject).</summary>
        public void ServerDespawn(NetworkObject obj)
        {
            if (_server == null || obj.Entity == null) return;
            _server.Despawn(obj.Entity);
            _serverObjects.Remove(obj);
            Destroy(obj.gameObject);
        }

        private void RegisterSchemas()
        {
            if (_prefabByArchetype.Count > 0) return;
            foreach (var prefab in registeredPrefabs)
            {
                if (prefab == null) continue;
                ReplicaRegistry.Register(prefab.BuildSchema());
                _prefabByArchetype[prefab.ArchetypeId] = prefab;
            }
        }

        private void Update()
        {
            // Apply queued spawns/despawns (posted from the network thread) on the main thread first.
            MainThreadDispatcher.Shared.Drain();

            if (_server != null)
                for (var i = 0; i < _serverObjects.Count; i++) _serverObjects[i].ServerSerialize();

            if (_client != null)
            {
                _client.Update();   // advance interpolation
                foreach (var kv in _clientObjects) kv.Value.ClientDeserialize();
            }
        }

        // Fired on the network receive thread → marshal onto the main thread before touching Unity objects.
        private void OnClientSpawn(NetworkEntityView view) => MainThreadDispatcher.Shared.Post(() =>
        {
            if (_clientObjects.ContainsKey(view.NetId)) return;
            if (!_prefabByArchetype.TryGetValue(view.ArchetypeId, out var prefab))
            {
                Debug.LogWarning($"[SetNet] No prefab registered for archetype {view.ArchetypeId}; entity {view.NetId} not spawned.");
                return;
            }
            var go = Instantiate(prefab);
            go.BindClient(view);
            _clientObjects[view.NetId] = go;
        });

        private void OnClientDespawn(NetworkEntityView view) => MainThreadDispatcher.Shared.Post(() =>
        {
            if (_clientObjects.TryGetValue(view.NetId, out var go))
            {
                _clientObjects.Remove(view.NetId);
                if (go != null) Destroy(go.gameObject);
            }
        });
    }
}
