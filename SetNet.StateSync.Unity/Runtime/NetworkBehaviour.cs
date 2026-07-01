using System.Collections.Generic;
using UnityEngine;
using SetNet.StateSync;

namespace SetNet.StateSync.Unity
{
    /// <summary>
    /// Base class for your own replicated gameplay components. Subclass it, declare your fields in
    /// <see cref="DeclareFields"/>, and read/write them in the same order in <see cref="Serialize"/> (server) and
    /// <see cref="Deserialize"/> (client). Use <see cref="IsOwner"/>/<see cref="IsServer"/> to branch authority.
    /// </summary>
    /// <example>
    /// <code>
    /// public sealed class Health : NetworkBehaviour
    /// {
    ///     public float Current = 100f;
    ///     public override void DeclareFields(List&lt;FieldDef&gt; f) =&gt; f.Add(new FieldDef(FieldType.Float, interpolate: true));
    ///     public override void Serialize(NetworkWriter w) =&gt; w.WriteFloat(Current);
    ///     public override void Deserialize(NetworkReader r) =&gt; Current = (float)r.ReadFloat();
    /// }
    /// </code>
    /// </example>
    public abstract class NetworkBehaviour : MonoBehaviour, INetworkComponent
    {
        /// <summary>The <see cref="NetworkObject"/> on this GameObject.</summary>
        protected NetworkObject NetworkObject { get; private set; } = null!;

        /// <summary>True on the client that owns this object.</summary>
        public bool IsOwner => NetworkObject != null && NetworkObject.IsOwner;

        /// <summary>True on the authoritative server.</summary>
        public bool IsServer => NetworkObject != null && NetworkObject.IsServer;

        /// <summary>Caches the sibling <see cref="NetworkObject"/>. Override, but call <c>base.Awake()</c>.</summary>
        protected virtual void Awake() => NetworkObject = GetComponent<NetworkObject>();

        /// <inheritdoc/>
        public abstract void DeclareFields(List<FieldDef> fields);

        /// <inheritdoc/>
        public abstract void Serialize(NetworkWriter writer);

        /// <inheritdoc/>
        public abstract void Deserialize(NetworkReader reader);
    }
}
