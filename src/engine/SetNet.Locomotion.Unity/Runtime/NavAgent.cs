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
        // One managed update per visible actor is expensive even when almost
        // every actor is idle. The application drives this sparse registry
        // once per frame through TickActive; SetPath/Stop maintain membership.
        private static readonly List<NavAgent> Active = new List<NavAgent>();

        private int _activeIndex = -1;

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

        /// <summary>How many agents currently have a live path.</summary>
        public static int ActiveCount => Active.Count;

        /// <summary>Sets a new path (the waypoints you got from a client-side <c>FindPath</c>). Replaces any current path.</summary>
        public void SetPath(IList<Vector3> waypoints)
        {
            _path.Clear();
            _index = 0;
            if (waypoints == null)
            {
                SetActive(false);
                return;
            }
            for (var i = 0; i < waypoints.Count; i++) _path.Add(waypoints[i]);
            // Skip only intermediate waypoints we're already standing on.
            // The final point never uses ArriveDistance: even a very short
            // command must be walked to its exact destination on a Tick.
            while (_index < _path.Count - 1 &&
                   (transform.position - _path[_index]).sqrMagnitude <= ArriveDistance * ArriveDistance)
            {
                _index++;
            }
            SetActive(_index < _path.Count);
        }

        /// <summary>Stops moving where it is (clears the path).</summary>
        public void Stop()
        {
            _path.Clear();
            _index = 0;
            SetActive(false);
        }

        /// <summary>
        /// Advances only agents which currently have a live path. Call once
        /// per frame from the application's central update loop.
        /// </summary>
        public static void TickActive(float dt)
        {
            for (var i = Active.Count - 1; i >= 0; i--)
            {
                var agent = Active[i];
                if (agent == null || !agent.isActiveAndEnabled || !agent.IsMoving)
                {
                    RemoveActiveAt(i);
                    continue;
                }

                agent.Tick(dt);
            }
        }

        /// <summary>
        /// Advances this agent once. Prefer <see cref="TickActive"/> when a
        /// scene contains many agents.
        /// </summary>
        public void Tick(float dt)
        {
            if (_index >= _path.Count) return;

            var target = _path[_index];
            var pos = transform.position;
            var next = Vector3.MoveTowards(pos, target, Speed * dt);
            transform.position = next;

            Face(next - pos, dt);

            bool final = _index == _path.Count - 1;
            bool reached = final
                // MoveTowards returns target itself when the remaining distance
                // fits this frame. Do not snap/assign it separately.
                ? (next - target).sqrMagnitude <= 1e-10f
                : (next - target).sqrMagnitude <= ArriveDistance * ArriveDistance;

            if (reached)
            {
                _index++;
                if (_index >= _path.Count)
                {
                    _path.Clear();
                    _index = 0;
                    SetActive(false);
                    Arrived?.Invoke();
                }
            }
        }

        private void OnEnable()
        {
            if (IsMoving) SetActive(true);
        }

        private void OnDisable() => SetActive(false);
        private void OnDestroy() => SetActive(false);

        private void SetActive(bool active)
        {
            if (active && isActiveAndEnabled)
            {
                if (_activeIndex >= 0) return;
                _activeIndex = Active.Count;
                Active.Add(this);
                return;
            }

            if (_activeIndex < 0) return;
            RemoveActiveAt(_activeIndex);
        }

        private static void RemoveActiveAt(int index)
        {
            if ((uint)index >= (uint)Active.Count) return;

            var removed = Active[index];
            var last = Active.Count - 1;
            var replacement = Active[last];
            Active[index] = replacement;
            Active.RemoveAt(last);

            if (replacement != null && index < Active.Count)
                replacement._activeIndex = index;
            if (removed != null)
                removed._activeIndex = -1;
        }

        private void Face(Vector3 delta, float dt)
        {
            if (TurnSpeed < 0f) return;
            delta.y = 0f;
            if (delta.sqrMagnitude < 1e-6f) return;
            var want = Quaternion.LookRotation(delta.normalized, Vector3.up);
            transform.rotation = TurnSpeed == 0f
                ? want
                : Quaternion.RotateTowards(transform.rotation, want, TurnSpeed * dt);
        }
    }
}
