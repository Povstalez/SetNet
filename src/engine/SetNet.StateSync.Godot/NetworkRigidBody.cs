using System.Collections.Generic;
using Godot;
using SetNet.Godot;
using SetNet.StateSync;

namespace SetNet.StateSync.Godot
{
    /// <summary>
    /// Replicates a <c>RigidBody3D</c>'s linear and angular velocity (interpolated). Set <see cref="Body"/> to the rigid
    /// body (or leave it and the component finds the first <c>RigidBody3D</c> child of the object). Pair with a
    /// <see cref="NetworkTransform"/> for pose; the velocity lets clients settle physics smoothly between snapshots.
    /// </summary>
    [GlobalClass]
    public partial class NetworkRigidBody : Node, INetworkComponent
    {
        /// <summary>The rigid body to sync. Defaults to the first <c>RigidBody3D</c> under the parent object.</summary>
        [Export] public RigidBody3D? Body { get; set; }

        [Export] public bool SyncLinearVelocity { get; set; } = true;
        [Export] public bool SyncAngularVelocity { get; set; } = true;

        private NetworkObject? _obj;

        /// <inheritdoc/>
        public override void _Ready()
        {
            _obj = GetParent() as NetworkObject;
            if (Body == null && _obj != null)
                foreach (var child in _obj.GetChildren())
                    if (child is RigidBody3D rb) { Body = rb; break; }
        }

        /// <inheritdoc/>
        public void DeclareFields(List<FieldDef> fields)
        {
            if (SyncLinearVelocity) fields.Add(new FieldDef(FieldType.Vector3, interpolate: true));
            if (SyncAngularVelocity) fields.Add(new FieldDef(FieldType.Vector3, interpolate: true));
        }

        /// <inheritdoc/>
        public void Serialize(NetworkWriter writer)
        {
            if (SyncLinearVelocity) writer.WriteVec3(Body != null ? Body.LinearVelocity.ToNet() : Vec3.Zero);
            if (SyncAngularVelocity) writer.WriteVec3(Body != null ? Body.AngularVelocity.ToNet() : Vec3.Zero);
        }

        /// <inheritdoc/>
        public void Deserialize(NetworkReader reader)
        {
            var lin = SyncLinearVelocity ? reader.ReadVec3().ToGodot() : (Vector3?)null;
            var ang = SyncAngularVelocity ? reader.ReadVec3().ToGodot() : (Vector3?)null;

            if (Body == null) return;
            if (_obj != null && _obj.IsOwner) return;   // owner runs its own physics

            if (lin.HasValue) Body.LinearVelocity = lin.Value;
            if (ang.HasValue) Body.AngularVelocity = ang.Value;
        }
    }
}
