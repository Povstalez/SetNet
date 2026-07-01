using System.Collections.Generic;
using UnityEngine;
using SetNet.StateSync;

namespace SetNet.StateSync.Unity
{
    /// <summary>
    /// Replicates an Animator: every controller parameter (Float interpolated; Int and Bool snapped; Trigger as a
    /// monotonic pulse counter) and, optionally, each layer's current state hash + normalized time so a client that
    /// missed a trigger still converges to the right state. Fire triggers through <see cref="SetTrigger"/> so they can be
    /// replicated. The parameter order comes from the shared AnimatorController, so server and clients agree on the schema.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public sealed class NetworkAnimator : MonoBehaviour, INetworkComponent
    {
        [Tooltip("Also sync each layer's current state hash + normalized time (helps recover a missed trigger).")]
        [SerializeField] private bool syncLayerStates = true;

        private Animator _animator = null!;
        private NetworkObject _obj = null!;
        private AnimatorControllerParameter[] _params = System.Array.Empty<AnimatorControllerParameter>();
        private readonly Dictionary<int, int> _triggerCounter = new Dictionary<int, int>();  // server: hash -> times fired
        private readonly Dictionary<int, int> _triggerSeen = new Dictionary<int, int>();      // client: hash -> last applied

        private Animator Animator => _animator != null ? _animator : (_animator = GetComponent<Animator>());

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _obj = GetComponent<NetworkObject>();
            _params = _animator.parameters;
        }

        /// <summary>Fires an animator trigger and replicates it to observers. Use this instead of <c>Animator.SetTrigger</c>.</summary>
        public void SetTrigger(string name) => SetTrigger(Animator.StringToHash(name));

        /// <summary>Fires an animator trigger (by hash) and replicates it.</summary>
        public void SetTrigger(int hash)
        {
            Animator.SetTrigger(hash);
            _triggerCounter.TryGetValue(hash, out var c);
            _triggerCounter[hash] = c + 1;
        }

        /// <inheritdoc/>
        public void DeclareFields(List<FieldDef> fields)
        {
            foreach (var p in Parameters())
            {
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: fields.Add(new FieldDef(FieldType.Float, interpolate: true)); break;
                    case AnimatorControllerParameterType.Int: fields.Add(new FieldDef(FieldType.Int)); break;
                    case AnimatorControllerParameterType.Bool: fields.Add(new FieldDef(FieldType.Bool)); break;
                    case AnimatorControllerParameterType.Trigger: fields.Add(new FieldDef(FieldType.Int)); break;   // pulse counter
                }
            }
            if (syncLayerStates)
                for (var layer = 0; layer < Animator.layerCount; layer++)
                {
                    fields.Add(new FieldDef(FieldType.Int));                       // current state full-path hash
                    fields.Add(new FieldDef(FieldType.Float, interpolate: true));  // normalized time
                }
        }

        /// <inheritdoc/>
        public void Serialize(NetworkWriter writer)
        {
            foreach (var p in Parameters())
            {
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: writer.WriteFloat(Animator.GetFloat(p.nameHash)); break;
                    case AnimatorControllerParameterType.Int: writer.WriteInt(Animator.GetInteger(p.nameHash)); break;
                    case AnimatorControllerParameterType.Bool: writer.WriteBool(Animator.GetBool(p.nameHash)); break;
                    case AnimatorControllerParameterType.Trigger:
                        _triggerCounter.TryGetValue(p.nameHash, out var c);
                        writer.WriteInt(c);
                        break;
                }
            }
            if (syncLayerStates)
                for (var layer = 0; layer < Animator.layerCount; layer++)
                {
                    var info = Animator.GetCurrentAnimatorStateInfo(layer);
                    writer.WriteInt(info.fullPathHash);
                    writer.WriteFloat(info.normalizedTime);
                }
        }

        /// <inheritdoc/>
        public void Deserialize(NetworkReader reader)
        {
            var apply = !_obj.IsOwner;   // owner drives its own animator
            foreach (var p in Parameters())
            {
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        { var v = reader.ReadFloat(); if (apply) Animator.SetFloat(p.nameHash, (float)v); break; }
                    case AnimatorControllerParameterType.Int:
                        { var v = reader.ReadInt(); if (apply) Animator.SetInteger(p.nameHash, (int)v); break; }
                    case AnimatorControllerParameterType.Bool:
                        { var v = reader.ReadBool(); if (apply) Animator.SetBool(p.nameHash, v); break; }
                    case AnimatorControllerParameterType.Trigger:
                        {
                            var counter = (int)reader.ReadInt();
                            if (apply)
                            {
                                _triggerSeen.TryGetValue(p.nameHash, out var seen);
                                if (counter > seen) { Animator.SetTrigger(p.nameHash); _triggerSeen[p.nameHash] = counter; }
                                else _triggerSeen[p.nameHash] = counter;
                            }
                            break;
                        }
                }
            }
            if (syncLayerStates)
                for (var layer = 0; layer < Animator.layerCount; layer++)
                {
                    var stateHash = (int)reader.ReadInt();
                    var normTime = (float)reader.ReadFloat();
                    if (apply && stateHash != 0 && Animator.GetCurrentAnimatorStateInfo(layer).fullPathHash != stateHash)
                        Animator.Play(stateHash, layer, normTime % 1f);
                }
        }

        private AnimatorControllerParameter[] Parameters()
        {
            if (_params.Length == 0) _params = Animator.parameters;
            return _params;
        }
    }
}
