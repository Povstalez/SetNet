using System.Collections.Generic;
using UnityEngine;
using SetNet.StateSync;

namespace SetNet.StateSync.Unity
{
    /// <summary>
    /// Marks a GameObject as a replicated entity. Sits alongside one or more <see cref="INetworkComponent"/> behaviours
    /// (<see cref="NetworkTransform"/>, <see cref="NetworkAnimator"/>, <see cref="NetworkRigidbody"/>, your own
    /// <see cref="NetworkBehaviour"/>s). The object's archetype schema is the ordered concatenation of those components'
    /// fields — so keep the component order identical across the prefab used on the server and clients (it is, since
    /// they share the prefab). <see cref="NetworkManager"/> drives serialization on the server and deserialization on
    /// clients.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetworkObject : MonoBehaviour
    {
        [Tooltip("Stable archetype id, unique per prefab. Must match between server and clients (it does — same prefab).")]
        [SerializeField] private ushort archetypeId;

        /// <summary>The archetype id (selects the schema / prefab).</summary>
        public ushort ArchetypeId => archetypeId;

        /// <summary>True on the authoritative server instance.</summary>
        public bool IsServer { get; private set; }

        /// <summary>True on the client that owns this object (eligible for local prediction).</summary>
        public bool IsOwner { get; internal set; }

        /// <summary>The server-side entity backing this object (server only).</summary>
        public NetworkEntity? Entity { get; private set; }

        /// <summary>The client-side view backing this object (client only).</summary>
        public NetworkEntityView? View { get; private set; }

        private INetworkComponent[] _components = System.Array.Empty<INetworkComponent>();

        private void Awake() => _components = GetComponents<INetworkComponent>();

        /// <summary>Builds this prefab's schema from its network components (called once per archetype at registration).</summary>
        public ReplicaSchema BuildSchema()
        {
            if (_components.Length == 0) _components = GetComponents<INetworkComponent>();
            var fields = new List<FieldDef>();
            foreach (var c in _components) c.DeclareFields(fields);
            var builder = ReplicaSchema.Create(archetypeId);
            foreach (var f in fields) builder.Field(f.Type, f.Interpolate, f.Precision);
            return builder.Build();
        }

        internal void BindServer(NetworkEntity entity) { IsServer = true; Entity = entity; }

        internal void BindClient(NetworkEntityView view) { View = view; IsOwner = view.IsOwner; }

        /// <summary>Server: pushes every component's current Unity state into the entity (called each frame by the manager).</summary>
        internal void ServerSerialize()
        {
            if (Entity == null) return;
            var index = 0;
            foreach (var c in _components)
            {
                c.Serialize(new NetworkWriter(Entity, index));
                index += CountFields(c);
            }
        }

        /// <summary>Client: applies the interpolated entity state to every component (called each frame by the manager).</summary>
        internal void ClientDeserialize()
        {
            if (View == null) return;
            IsOwner = View.IsOwner;
            var index = 0;
            foreach (var c in _components)
            {
                c.Deserialize(new NetworkReader(View, index));
                index += CountFields(c);
            }
        }

        private static int CountFields(INetworkComponent c)
        {
            var tmp = new List<FieldDef>();
            c.DeclareFields(tmp);
            return tmp.Count;
        }
    }
}
