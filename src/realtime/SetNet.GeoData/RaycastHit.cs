namespace SetNet.GeoData
{
    /// <summary>The result of a raycast against the world geometry.</summary>
    public readonly struct RaycastHit
    {
        /// <summary>Whether the ray hit anything within its max distance.</summary>
        public readonly bool Hit;
        /// <summary>The hit point (valid only when <see cref="Hit"/>).</summary>
        public readonly Vec3 Point;
        /// <summary>Distance from the ray origin to the hit.</summary>
        public readonly float Distance;
        /// <summary>The surface normal at the hit (valid only when <see cref="Hit"/>).</summary>
        public readonly Vec3 Normal;

        /// <summary>Creates a hit result.</summary>
        public RaycastHit(bool hit, Vec3 point, float distance, Vec3 normal)
        {
            Hit = hit; Point = point; Distance = distance; Normal = normal;
        }

        /// <summary>A "no hit" result.</summary>
        public static readonly RaycastHit None = new RaycastHit(false, Vec3.Zero, float.PositiveInfinity, Vec3.Zero);
    }
}
