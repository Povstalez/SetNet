using System;

namespace SetNet.GeoData
{
    /// <summary>
    /// A minimal 3D vector — GeoData is engine-agnostic and depends only on <c>SetNet</c>, so it ships its own value
    /// type instead of pulling in a math library. Convert to/from your engine's vector at the edges.
    /// </summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        /// <summary>X component.</summary>
        public readonly float X;
        /// <summary>Y component (up).</summary>
        public readonly float Y;
        /// <summary>Z component.</summary>
        public readonly float Z;

        /// <summary>Creates a vector.</summary>
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        /// <summary>The zero vector.</summary>
        public static readonly Vec3 Zero = new Vec3(0, 0, 0);

        /// <summary>Component-wise sum.</summary>
        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        /// <summary>Component-wise difference.</summary>
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        /// <summary>Scalar multiply.</summary>
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        /// <summary>Scalar multiply.</summary>
        public static Vec3 operator *(float s, Vec3 a) => a * s;
        /// <summary>Negation.</summary>
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);

        /// <summary>Euclidean length.</summary>
        public float Length => MathF.Sqrt(X * X + Y * Y + Z * Z);
        /// <summary>Squared length (cheaper than <see cref="Length"/>).</summary>
        public float LengthSquared => X * X + Y * Y + Z * Z;

        /// <summary>Unit vector in the same direction (returns zero for a zero vector).</summary>
        public Vec3 Normalized
        {
            get { var len = Length; return len > 1e-6f ? this * (1f / len) : Zero; }
        }

        /// <summary>Distance between two points.</summary>
        public static float Distance(Vec3 a, Vec3 b) => (a - b).Length;
        /// <summary>Squared distance between two points.</summary>
        public static float DistanceSquared(Vec3 a, Vec3 b) => (a - b).LengthSquared;
        /// <summary>Horizontal (XZ-plane) distance, ignoring height.</summary>
        public static float HorizontalDistance(Vec3 a, Vec3 b)
        {
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Dot product.</summary>
        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        /// <summary>Cross product.</summary>
        public static Vec3 Cross(Vec3 a, Vec3 b)
            => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        /// <summary>Linear interpolation.</summary>
        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => a + (b - a) * t;

        /// <inheritdoc/>
        public bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;
        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Vec3 v && Equals(v);
        /// <inheritdoc/>
        public override int GetHashCode() { unchecked { return (X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode(); } }
        /// <inheritdoc/>
        public override string ToString() => $"({X:0.##}, {Y:0.##}, {Z:0.##})";
    }
}
