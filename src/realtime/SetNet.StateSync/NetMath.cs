using System;

namespace SetNet.StateSync
{
    /// <summary>Engine-agnostic 2D vector (the core has no UnityEngine dependency; the Unity layer converts to/from <c>Vector2</c>).</summary>
    public readonly struct Vec2
    {
        /// <summary>X component.</summary>
        public readonly float X;
        /// <summary>Y component.</summary>
        public readonly float Y;

        /// <summary>Creates a 2D vector.</summary>
        public Vec2(float x, float y) { X = x; Y = y; }

        /// <summary>Linear interpolation between <paramref name="a"/> and <paramref name="b"/> by <paramref name="t"/>.</summary>
        public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => new Vec2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    /// <summary>Engine-agnostic 3D vector.</summary>
    public readonly struct Vec3
    {
        /// <summary>X component.</summary>
        public readonly float X;
        /// <summary>Y component.</summary>
        public readonly float Y;
        /// <summary>Z component.</summary>
        public readonly float Z;

        /// <summary>Creates a 3D vector.</summary>
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        /// <summary>The zero vector.</summary>
        public static Vec3 Zero => new Vec3(0, 0, 0);

        /// <summary>Linear interpolation between <paramref name="a"/> and <paramref name="b"/> by <paramref name="t"/>.</summary>
        public static Vec3 Lerp(Vec3 a, Vec3 b, float t)
            => new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        /// <summary>Squared distance between two points (cheaper than distance; good for interest checks).</summary>
        public static float DistanceSquared(Vec3 a, Vec3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }

    /// <summary>Engine-agnostic quaternion (rotation).</summary>
    public readonly struct Quat
    {
        /// <summary>X component.</summary>
        public readonly float X;
        /// <summary>Y component.</summary>
        public readonly float Y;
        /// <summary>Z component.</summary>
        public readonly float Z;
        /// <summary>W component.</summary>
        public readonly float W;

        /// <summary>Creates a quaternion.</summary>
        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        /// <summary>The identity rotation.</summary>
        public static Quat Identity => new Quat(0, 0, 0, 1);

        /// <summary>
        /// Normalized linear interpolation (nlerp) between two rotations — cheaper than slerp and good enough for the
        /// small deltas between adjacent snapshots. Handles the double-cover by flipping <paramref name="b"/> to the
        /// nearer hemisphere.
        /// </summary>
        public static Quat Nlerp(Quat a, Quat b, float t)
        {
            var dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            var s = dot < 0 ? -1f : 1f;   // take the shorter arc
            var x = a.X + (b.X * s - a.X) * t;
            var y = a.Y + (b.Y * s - a.Y) * t;
            var z = a.Z + (b.Z * s - a.Z) * t;
            var w = a.W + (b.W * s - a.W) * t;
            var len = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            if (len < 1e-6f) return Identity;
            var inv = 1f / len;
            return new Quat(x * inv, y * inv, z * inv, w * inv);
        }
    }
}
