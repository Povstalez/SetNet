using UnityEngine;

namespace SetNet.Locomotion.Unity
{
    /// <summary>
    /// The pure math of one <see cref="NavAgent"/> step, extracted so a
    /// Burst-compiled job can execute the exact same code the managed
    /// per-agent path runs.
    ///
    /// <para>
    /// <b>Why a shared function instead of a port.</b> The alternative — a
    /// hand-ported copy in Unity.Mathematics — inevitably drifts: Vector3's
    /// equality is an epsilon test (sqrMagnitude &lt; 1e-10), Mathf.Lerp clamps
    /// while math.lerp doesn't, MoveTowards returns the target itself when the
    /// remaining distance fits the step. Any of those differences moves an
    /// agent to a slightly different point than the server-mirrored legacy
    /// path would, and the gap accumulates every frame. Sharing one body makes
    /// parity a property of the build, not of a test suite.
    /// </para>
    ///
    /// <para>
    /// Burst compiles UnityEngine's Vector3/Quaternion operations natively, so
    /// nothing here needs float3/quaternion counterparts.
    /// </para>
    /// </summary>
    public static class NavAgentMath
    {
        /// <summary>
        /// One movement step toward the current waypoint — the body of
        /// <see cref="NavAgent.Tick"/>'s translation, verbatim.
        /// Returns true when the waypoint counts as reached this step.
        /// </summary>
        /// <remarks>
        /// The final point never uses <paramref name="arriveDistance"/>: even a
        /// very short command must be walked to its exact destination.
        /// MoveTowards returns the target itself when the remaining distance
        /// fits this frame — do not snap/assign it separately.
        /// </remarks>
        public static bool StepTowards(ref Vector3 position, Vector3 target,
                                       float speed, float dt,
                                       bool final, float arriveDistance)
        {
            var next = Vector3.MoveTowards(position, target, speed * dt);
            position = next;

            return final
                ? (next - target).sqrMagnitude <= 1e-10f
                : (next - target).sqrMagnitude <= arriveDistance * arriveDistance;
        }

        /// <summary>
        /// Turning toward the movement direction — the semantics of
        /// <see cref="NavAgent"/>'s original Face, rebuilt on hand-rolled
        /// quaternion math.
        /// </summary>
        /// <remarks>
        /// Why not Quaternion.LookRotation/RotateTowards: those are engine
        /// externs, and Burst cannot compile engine calls — the whole job
        /// would silently fall back to managed. The delta is flattened to the
        /// XZ plane first (as the original always did), so the target is a
        /// pure yaw and both the look-at and the clamped approach reduce to
        /// plain trigonometry. The managed path calls this same body, so both
        /// sides turn identically.
        /// </remarks>
        /// <returns>True when the rotation actually changed — callers use it
        /// to skip the transform write for agents that never turn.</returns>
        public static bool Face(ref Quaternion rotation, Vector3 delta,
                                float turnSpeed, float dt)
        {
            if (turnSpeed < 0f) return false;
            delta.y = 0f;
            if (delta.sqrMagnitude < 1e-6f) return false;

            var want = Yaw(Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg);
            rotation = turnSpeed == 0f
                ? want
                : RotateTowards(rotation, want, turnSpeed * dt);
            return true;
        }

        /// <summary>Pure-yaw rotation from degrees around +Y — Euler(0, deg, 0) without the engine call.</summary>
        public static Quaternion Yaw(float degrees)
        {
            float half = degrees * 0.5f * Mathf.Deg2Rad;
            return new Quaternion(0f, Mathf.Sin(half), 0f, Mathf.Cos(half));
        }

        /// <summary>
        /// Quaternion.RotateTowards without the engine extern: the angle from
        /// the dot product, then a clamped slerp. Handles the double-cover
        /// (negative dot) the same way the engine does.
        /// </summary>
        public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegrees)
        {
            float dot = from.x * to.x + from.y * to.y + from.z * to.z + from.w * to.w;
            float angle = Mathf.Acos(Mathf.Min(Mathf.Abs(dot), 1f)) * 2f * Mathf.Rad2Deg;
            if (angle <= maxDegrees || angle < 1e-4f) return to;
            return Slerp(from, to, maxDegrees / angle);
        }

        /// <summary>Slerp without the engine extern. Unclamped t is fine here — callers clamp.</summary>
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (dot < 0f)
            {
                b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
                dot = -dot;
            }

            float s0, s1;
            if (dot > 0.9995f)
            {
                // Nearly collinear — go linear, the sine below would divide by zero.
                s0 = 1f - t;
                s1 = t;
            }
            else
            {
                float theta = Mathf.Acos(dot);
                float sin = Mathf.Sin(theta);
                s0 = Mathf.Sin((1f - t) * theta) / sin;
                s1 = Mathf.Sin(t * theta) / sin;
            }

            var q = new Quaternion(
                s0 * a.x + s1 * b.x,
                s0 * a.y + s1 * b.y,
                s0 * a.z + s1 * b.z,
                s0 * a.w + s1 * b.w);

            // Normalize: both the linear branch and accumulated error would
            // otherwise slowly denormalize the rotation.
            float len = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return len > 1e-9f
                ? new Quaternion(q.x / len, q.y / len, q.z / len, q.w / len)
                : b;
        }
    }
}
