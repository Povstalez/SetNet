using Godot;
using SetNet.StateSync;

namespace SetNet.Godot
{
    /// <summary>Conversions between the engine-agnostic SetNet.StateSync math types and Godot's <c>Vector2/Vector3/Quaternion</c>.</summary>
    public static class GodotConversions
    {
        /// <summary>Godot vector → core <see cref="Vec3"/>.</summary>
        public static Vec3 ToNet(this Vector3 v) => new Vec3(v.X, v.Y, v.Z);
        /// <summary>Core <see cref="Vec3"/> → Godot vector.</summary>
        public static Vector3 ToGodot(this Vec3 v) => new Vector3(v.X, v.Y, v.Z);

        /// <summary>Godot 2D vector → core <see cref="Vec2"/>.</summary>
        public static Vec2 ToNet(this Vector2 v) => new Vec2(v.X, v.Y);
        /// <summary>Core <see cref="Vec2"/> → Godot 2D vector.</summary>
        public static Vector2 ToGodot(this Vec2 v) => new Vector2(v.X, v.Y);

        /// <summary>Godot quaternion → core <see cref="Quat"/>.</summary>
        public static Quat ToNet(this Quaternion q) => new Quat(q.X, q.Y, q.Z, q.W);
        /// <summary>Core <see cref="Quat"/> → Godot quaternion.</summary>
        public static Quaternion ToGodot(this Quat q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }
}
