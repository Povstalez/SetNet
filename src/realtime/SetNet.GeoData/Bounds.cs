namespace SetNet.GeoData
{
    /// <summary>An axis-aligned bounding box in world space.</summary>
    public readonly struct Bounds
    {
        /// <summary>The minimum corner.</summary>
        public readonly Vec3 Min;
        /// <summary>The maximum corner.</summary>
        public readonly Vec3 Max;

        /// <summary>Creates a box from its two corners.</summary>
        public Bounds(Vec3 min, Vec3 max) { Min = min; Max = max; }

        /// <summary>The box centre.</summary>
        public Vec3 Center => (Min + Max) * 0.5f;
        /// <summary>The box size (Max - Min).</summary>
        public Vec3 Size => Max - Min;

        /// <summary>True if the point lies within the box (inclusive).</summary>
        public bool Contains(Vec3 p)
            => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;
    }
}
