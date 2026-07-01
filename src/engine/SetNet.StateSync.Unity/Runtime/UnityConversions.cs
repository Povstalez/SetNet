using UnityEngine;
using SetNet.StateSync;

namespace SetNet.StateSync.Unity
{
    /// <summary>Conversions between the engine-agnostic core math types and UnityEngine's <c>Vector2/Vector3/Quaternion</c>.</summary>
    public static class UnityConversions
    {
        /// <summary>Converts a UnityEngine vector to the core <see cref="Vec3"/>.</summary>
        public static Vec3 ToNet(this Vector3 v) => new Vec3(v.x, v.y, v.z);

        /// <summary>Converts a core <see cref="Vec3"/> to a UnityEngine vector.</summary>
        public static Vector3 ToUnity(this Vec3 v) => new Vector3(v.X, v.Y, v.Z);

        /// <summary>Converts a UnityEngine 2D vector to the core <see cref="Vec2"/>.</summary>
        public static Vec2 ToNet(this Vector2 v) => new Vec2(v.x, v.y);

        /// <summary>Converts a core <see cref="Vec2"/> to a UnityEngine 2D vector.</summary>
        public static Vector2 ToUnity(this Vec2 v) => new Vector2(v.X, v.Y);

        /// <summary>Converts a UnityEngine quaternion to the core <see cref="Quat"/>.</summary>
        public static Quat ToNet(this Quaternion q) => new Quat(q.x, q.y, q.z, q.w);

        /// <summary>Converts a core <see cref="Quat"/> to a UnityEngine quaternion.</summary>
        public static Quaternion ToUnity(this Quat q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }
}
