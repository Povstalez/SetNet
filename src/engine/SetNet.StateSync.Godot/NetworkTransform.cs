using System.Collections.Generic;
using Godot;
using SetNet.Godot;
using SetNet.StateSync;

namespace SetNet.StateSync.Godot
{
    /// <summary>
    /// Replicates a <c>Node3D</c>'s transform: position and rotation (interpolated), with optional scale. Add it as a
    /// child of the <see cref="NetworkObject"/> whose transform you want synced (the default target is the parent
    /// <see cref="NetworkObject"/>). The owner can optionally keep authority over its own transform (client prediction).
    /// </summary>
    [GlobalClass]
    public partial class NetworkTransform : Node, INetworkComponent
    {
        /// <summary>The node to sync. Defaults to the parent <see cref="NetworkObject"/> if left unset.</summary>
        [Export] public Node3D? Target { get; set; }

        [Export] public bool SyncPosition { get; set; } = true;
        [Export] public bool SyncRotation { get; set; } = true;
        [Export] public bool SyncScale { get; set; } = false;

        /// <summary>Position quantization step in world units (0 = raw float). E.g. 0.001 = mm precision, smaller packets.</summary>
        [Export] public float PositionPrecision { get; set; } = 0f;

        /// <summary>If set, the owning client keeps its own transform (prediction) and ignores incoming pose for itself.</summary>
        [Export] public bool OwnerAuthoritative { get; set; } = false;

        private NetworkObject? _obj;

        /// <inheritdoc/>
        public override void _Ready()
        {
            _obj = GetParent() as NetworkObject;
            Target ??= _obj;
        }

        /// <inheritdoc/>
        public void DeclareFields(List<FieldDef> fields)
        {
            if (SyncPosition) fields.Add(new FieldDef(FieldType.Vector3, interpolate: true, precision: PositionPrecision));
            if (SyncRotation) fields.Add(new FieldDef(FieldType.Quaternion, interpolate: true));
            if (SyncScale) fields.Add(new FieldDef(FieldType.Vector3, interpolate: true));
        }

        /// <inheritdoc/>
        public void Serialize(NetworkWriter writer)
        {
            var t = Target;
            if (t == null) return;
            if (SyncPosition) writer.WriteVec3(t.Position.ToNet());
            if (SyncRotation) writer.WriteQuat(t.Quaternion.ToNet());
            if (SyncScale) writer.WriteVec3(t.Scale.ToNet());
        }

        /// <inheritdoc/>
        public void Deserialize(NetworkReader reader)
        {
            var t = Target;
            var pos = SyncPosition ? reader.ReadVec3().ToGodot() : (Vector3?)null;
            var rot = SyncRotation ? reader.ReadQuat().ToGodot() : (Quaternion?)null;
            var scale = SyncScale ? reader.ReadVec3().ToGodot() : (Vector3?)null;

            if (t == null) return;
            if (OwnerAuthoritative && _obj != null && _obj.IsOwner) return;   // predicted locally — don't overwrite

            if (pos.HasValue) t.Position = pos.Value;
            if (rot.HasValue) t.Quaternion = rot.Value;
            if (scale.HasValue) t.Scale = scale.Value;
        }
    }
}
