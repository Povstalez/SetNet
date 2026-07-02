using System.Collections.Generic;
using Godot;
using SetNet.StateSync;

namespace SetNet.StateSync.Godot
{
    /// <summary>
    /// Base class for your own replicated gameplay components in Godot. Subclass it (as a child node of a
    /// <see cref="NetworkObject"/>), declare your fields in <see cref="DeclareFields"/>, and read/write them in the same
    /// order in <see cref="Serialize"/> (server) and <see cref="Deserialize"/> (client). Use <see cref="IsOwner"/> /
    /// <see cref="IsServer"/> to branch authority.
    /// </summary>
    public abstract partial class NetworkBehaviour : Node, INetworkComponent
    {
        /// <summary>The <see cref="NetworkObject"/> this component belongs to (its parent).</summary>
        protected NetworkObject? NetworkObject { get; private set; }

        /// <summary>True on the client that owns this object.</summary>
        public bool IsOwner => NetworkObject?.IsOwner ?? false;

        /// <summary>True on the authoritative server.</summary>
        public bool IsServer => NetworkObject?.IsServer ?? false;

        /// <inheritdoc/>
        public override void _Ready() => NetworkObject = GetParent() as NetworkObject;

        /// <inheritdoc/>
        public abstract void DeclareFields(List<FieldDef> fields);

        /// <inheritdoc/>
        public abstract void Serialize(NetworkWriter writer);

        /// <inheritdoc/>
        public abstract void Deserialize(NetworkReader reader);
    }
}
