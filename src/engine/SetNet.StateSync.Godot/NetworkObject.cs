using System;
using System.Collections.Generic;
using Godot;
using SetNet.StateSync;

namespace SetNet.StateSync.Godot
{
    /// <summary>
    /// The root of a replicated scene (a <c>Node3D</c>). Add sync components as children —
    /// <see cref="NetworkTransform"/>, <see cref="NetworkRigidBody"/>, <see cref="NetworkAnimationPlayer"/>, or your own
    /// <see cref="NetworkBehaviour"/>. The object's archetype schema is the ordered concatenation of those children's
    /// fields, so keep the child order identical across the scene used on the server and clients (it is — same
    /// <c>PackedScene</c>). <see cref="NetworkManager"/> drives serialization on the server and deserialization on clients.
    /// </summary>
    [GlobalClass]
    public partial class NetworkObject : Node3D
    {
        /// <summary>Stable archetype id, unique per scene. Must match between server and clients (it does — same scene).</summary>
        [Export] public int ArchetypeId { get; set; }

        /// <summary>The archetype id as the <c>ushort</c> the core uses.</summary>
        public ushort Archetype => (ushort)ArchetypeId;

        /// <summary>True on the authoritative server instance.</summary>
        public bool IsServer { get; private set; }

        /// <summary>True on the client that owns this object (eligible for local prediction).</summary>
        public bool IsOwner { get; internal set; }

        /// <summary>The server-side entity backing this object (server only).</summary>
        public NetworkEntity? Entity { get; private set; }

        /// <summary>The client-side view backing this object (client only).</summary>
        public NetworkEntityView? View { get; private set; }

        private INetworkComponent[] _components = Array.Empty<INetworkComponent>();

        /// <inheritdoc/>
        public override void _Ready() => _components = Gather();

        private INetworkComponent[] Gather()
        {
            var list = new List<INetworkComponent>();
            foreach (var child in GetChildren())
                if (child is INetworkComponent c) list.Add(c);
            return list.ToArray();
        }

        /// <summary>Builds this scene's schema from its network components (called once per archetype at registration).</summary>
        public ReplicaSchema BuildSchema()
        {
            if (_components.Length == 0) _components = Gather();
            var fields = new List<FieldDef>();
            foreach (var c in _components) c.DeclareFields(fields);
            var builder = ReplicaSchema.Create(Archetype);
            foreach (var f in fields) builder.Field(f.Type, f.Interpolate, f.Precision);
            return builder.Build();
        }

        internal void BindServer(NetworkEntity entity) { IsServer = true; Entity = entity; }

        internal void BindClient(NetworkEntityView view) { View = view; IsOwner = view.IsOwner; }

        internal void ServerSerialize()
        {
            if (Entity == null) return;
            var index = 0;
            foreach (var c in _components) { c.Serialize(new NetworkWriter(Entity, index)); index += CountFields(c); }
        }

        internal void ClientDeserialize()
        {
            if (View == null) return;
            IsOwner = View.IsOwner;
            var index = 0;
            foreach (var c in _components) { c.Deserialize(new NetworkReader(View, index)); index += CountFields(c); }
        }

        private static int CountFields(INetworkComponent c)
        {
            var tmp = new List<FieldDef>();
            c.DeclareFields(tmp);
            return tmp.Count;
        }
    }
}
