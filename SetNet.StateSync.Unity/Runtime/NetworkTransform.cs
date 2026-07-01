using System.Collections.Generic;
using UnityEngine;
using SetNet.StateSync;

namespace SetNet.StateSync.Unity
{
    /// <summary>
    /// Replicates a Transform: position and rotation (interpolated), with optional scale and a choice of local vs world
    /// space. Client instances read the interpolated pose each frame; the owner can optionally keep authority over its
    /// own transform (client prediction) by not applying incoming pose to itself.
    /// </summary>
    public sealed class NetworkTransform : MonoBehaviour, INetworkComponent
    {
        [SerializeField] private bool syncPosition = true;
        [SerializeField] private bool syncRotation = true;
        [SerializeField] private bool syncScale = false;
        [SerializeField] private bool useLocalSpace = false;

        [Tooltip("Position quantization step in world units (0 = raw float). E.g. 0.001 = mm precision, smaller packets.")]
        [SerializeField] private float positionPrecision = 0f;

        [Tooltip("If set, the owning client keeps its own transform (prediction) and ignores incoming pose for itself.")]
        [SerializeField] private bool ownerAuthoritative = false;

        private NetworkObject _obj = null!;

        private void Awake() => _obj = GetComponent<NetworkObject>();

        /// <inheritdoc/>
        public void DeclareFields(List<FieldDef> fields)
        {
            if (syncPosition) fields.Add(new FieldDef(FieldType.Vector3, interpolate: true, precision: positionPrecision));
            if (syncRotation) fields.Add(new FieldDef(FieldType.Quaternion, interpolate: true));
            if (syncScale) fields.Add(new FieldDef(FieldType.Vector3, interpolate: true));
        }

        /// <inheritdoc/>
        public void Serialize(NetworkWriter writer)
        {
            var t = transform;
            if (syncPosition) writer.WriteVec3((useLocalSpace ? t.localPosition : t.position).ToNet());
            if (syncRotation) writer.WriteQuat((useLocalSpace ? t.localRotation : t.rotation).ToNet());
            if (syncScale) writer.WriteVec3(t.localScale.ToNet());
        }

        /// <inheritdoc/>
        public void Deserialize(NetworkReader reader)
        {
            var t = transform;
            var pos = syncPosition ? reader.ReadVec3().ToUnity() : (Vector3?)null;
            var rot = syncRotation ? reader.ReadQuat().ToUnity() : (Quaternion?)null;
            var scale = syncScale ? reader.ReadVec3().ToUnity() : (Vector3?)null;

            if (ownerAuthoritative && _obj.IsOwner) return;   // predicted locally — don't overwrite

            if (pos.HasValue) { if (useLocalSpace) t.localPosition = pos.Value; else t.position = pos.Value; }
            if (rot.HasValue) { if (useLocalSpace) t.localRotation = rot.Value; else t.rotation = rot.Value; }
            if (scale.HasValue) t.localScale = scale.Value;
        }
    }
}
