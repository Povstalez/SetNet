using System;
using System.Collections.Generic;
using UnityEngine;

namespace SetNet.Locomotion.Unity
{
    /// <summary>
    /// Moves this GameObject along a client-side path at <see cref="Speed"/>. The server is authoritative and sends only
    /// the <i>destination point</i>; on the client you compute a path with <c>SetNet.PathFinding</c> from your local
    /// geodata and hand its waypoints to <see cref="SetPath"/> — this component then walks the model there, turning to
    /// face the way it goes. Set <see cref="Speed"/> from your replicated move-speed stat.
    /// </summary>
    /// <remarks>Pure UnityEngine — it doesn't reference SetNet, so convert your <c>SetNet.GeoData.Vec3</c> waypoints to
    /// <see cref="Vector3"/> before calling <see cref="SetPath"/> (they share X/Y/Z, so it's component-wise).</remarks>
    [DisallowMultipleComponent]
    public sealed class NavAgent : MonoBehaviour
    {
        [Tooltip("World units per second — set this from your replicated move-speed stat.")]
        public float Speed = 4f;

        [Tooltip("How close (world units) counts as reaching a waypoint.")]
        public float ArriveDistance = 0.05f;

        [Tooltip("Degrees/sec the model turns toward its movement direction (0 = snap instantly, <0 = don't rotate).")]
        public float TurnSpeed = 720f;

        private readonly List<Vector3> _path = new List<Vector3>();
        private int _index;

        /// <summary>True while there are remaining waypoints to walk.</summary>
        public bool IsMoving => _index < _path.Count;

        /// <summary>The final destination of the current path, or null when idle.</summary>
        public Vector3? Destination => _path.Count > 0 ? _path[_path.Count - 1] : (Vector3?)null;

        /// <summary>Raised once when the last waypoint is reached.</summary>
        public event Action Arrived;

        /// <summary>Sets a new path (the waypoints you got from a client-side <c>FindPath</c>). Replaces any current path.</summary>
        public void SetPath(IList<Vector3> waypoints)
        {
            _path.Clear();
            _index = 0;
            if (waypoints == null) return;
            for (var i = 0; i < waypoints.Count; i++) _path.Add(waypoints[i]);
            // Skip a first waypoint we're already standing on.
            while (_index < _path.Count && (transform.position - _path[_index]).sqrMagnitude <= ArriveDistance * ArriveDistance)
                _index++;
        }

        /// <summary>Stops moving where it is (clears the path).</summary>
        public void Stop()
        {
            _path.Clear();
            _index = 0;
        }

        private void Update()
        {
            if (_index >= _path.Count) return;

            var target = _path[_index];
            var pos = transform.position;
            var next = Vector3.MoveTowards(pos, target, Speed * Time.deltaTime);
            transform.position = next;

            Face(next - pos);

            if ((next - target).sqrMagnitude <= ArriveDistance * ArriveDistance)
            {
                _index++;
                if (_index >= _path.Count)
                {
                    _path.Clear();
                    _index = 0;
                    Arrived?.Invoke();
                }
            }
        }

        private void Face(Vector3 delta)
        {
            if (TurnSpeed < 0f) return;
            delta.y = 0f;
            if (delta.sqrMagnitude < 1e-6f) return;
            var want = Quaternion.LookRotation(delta.normalized, Vector3.up);
            transform.rotation = TurnSpeed == 0f
                ? want
                : Quaternion.RotateTowards(transform.rotation, want, TurnSpeed * Time.deltaTime);
        }
    }
}
