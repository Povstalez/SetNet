using System.Collections.Generic;
using UnityEngine;
using SetNet.StateSync;

namespace SetNet.StateSync.Unity
{
    /// <summary>
    /// Replicates a Rigidbody's linear and angular velocity (interpolated). Pair it with a <see cref="NetworkTransform"/>
    /// for pose; the velocity here lets clients extrapolate/settle physics smoothly between snapshots. On non-owner
    /// clients the rigidbody is made kinematic so incoming pose isn't fought by the local physics solver.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class NetworkRigidbody : MonoBehaviour, INetworkComponent
    {
        [SerializeField] private bool syncVelocity = true;
        [SerializeField] private bool syncAngularVelocity = true;

        private Rigidbody _rb = null!;
        private NetworkObject _obj = null!;
        private bool _configured;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _obj = GetComponent<NetworkObject>();
        }

        private void Start()
        {
            // On clients that don't own/simulate this body, let the network drive the transform instead of local physics.
            if (!_obj.IsServer && !_obj.IsOwner) _rb.isKinematic = true;
            _configured = true;
        }

        /// <inheritdoc/>
        public void DeclareFields(List<FieldDef> fields)
        {
            if (syncVelocity) fields.Add(new FieldDef(FieldType.Vector3, interpolate: true));
            if (syncAngularVelocity) fields.Add(new FieldDef(FieldType.Vector3, interpolate: true));
        }

        /// <inheritdoc/>
        public void Serialize(NetworkWriter writer)
        {
            if (syncVelocity) writer.WriteVec3(_rb.velocity.ToNet());
            if (syncAngularVelocity) writer.WriteVec3(_rb.angularVelocity.ToNet());
        }

        /// <inheritdoc/>
        public void Deserialize(NetworkReader reader)
        {
            var vel = syncVelocity ? reader.ReadVec3().ToUnity() : (Vector3?)null;
            var ang = syncAngularVelocity ? reader.ReadVec3().ToUnity() : (Vector3?)null;

            if (_obj.IsOwner) return;                 // owner runs its own physics
            if (_configured && _rb.isKinematic) return;   // kinematic: transform is driven by NetworkTransform

            if (vel.HasValue) _rb.velocity = vel.Value;
            if (ang.HasValue) _rb.angularVelocity = ang.Value;
        }
    }
}
