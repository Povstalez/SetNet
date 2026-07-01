using System;
using System.IO;

namespace SetNet.StateSync
{
    /// <summary>
    /// A single replicated field's value, stored as a small union: numeric types share <see cref="Num"/>, vectors use
    /// <see cref="V3"/> (Vector2 uses X/Y), quaternions use <see cref="Q"/>, and strings use <see cref="Str"/>. Only the
    /// slot matching the field's <see cref="FieldType"/> is meaningful. Kept deliberately simple (correctness first);
    /// a future version can pack these more tightly.
    /// </summary>
    public struct FieldValue : IEquatable<FieldValue>
    {
        /// <summary>Numeric payload for Bool (0/1), Byte, Int, UInt, Long, Float and Double.</summary>
        public double Num;
        /// <summary>Vector payload for Vector2 (X,Y) and Vector3.</summary>
        public Vec3 V3;
        /// <summary>Quaternion payload.</summary>
        public Quat Q;
        /// <summary>String payload.</summary>
        public string? Str;

        /// <summary>Wraps a numeric value.</summary>
        public static FieldValue Number(double n) => new FieldValue { Num = n };
        /// <summary>Wraps a 2D vector.</summary>
        public static FieldValue Vector2(Vec2 v) => new FieldValue { V3 = new Vec3(v.X, v.Y, 0) };
        /// <summary>Wraps a 3D vector.</summary>
        public static FieldValue Vector3(Vec3 v) => new FieldValue { V3 = v };
        /// <summary>Wraps a quaternion.</summary>
        public static FieldValue Quaternion(Quat q) => new FieldValue { Q = q };
        /// <summary>Wraps a string.</summary>
        public static FieldValue String(string? s) => new FieldValue { Str = s ?? "" };

        /// <summary>Reads this value's Vector2 view.</summary>
        public Vec2 AsVec2() => new Vec2((float)V3.X, (float)V3.Y);

        /// <summary>The default value for a field of the given type (numeric 0, zero vector, identity rotation, empty string).</summary>
        public static FieldValue Default(FieldType type) => type switch
        {
            FieldType.Quaternion => Quaternion(Quat.Identity),
            FieldType.String => String(""),
            _ => new FieldValue(),
        };

        /// <summary>Writes this value to the wire per its field definition (applying float quantization when configured).</summary>
        public void Write(BinaryWriter w, FieldDef def)
        {
            switch (def.Type)
            {
                case FieldType.Bool: w.Write(Num != 0); break;
                case FieldType.Byte: w.Write((byte)Num); break;
                case FieldType.Int: w.Write((int)Num); break;
                case FieldType.UInt: w.Write((uint)Num); break;
                case FieldType.Long: w.Write((long)Num); break;
                case FieldType.Double: w.Write(Num); break;
                case FieldType.String: w.Write(Str ?? ""); break;
                case FieldType.Float: WriteScalar(w, (float)Num, def.Precision); break;
                case FieldType.Vector2:
                    WriteScalar(w, (float)V3.X, def.Precision);
                    WriteScalar(w, (float)V3.Y, def.Precision);
                    break;
                case FieldType.Vector3:
                    WriteScalar(w, (float)V3.X, def.Precision);
                    WriteScalar(w, (float)V3.Y, def.Precision);
                    WriteScalar(w, (float)V3.Z, def.Precision);
                    break;
                case FieldType.Quaternion:
                    w.Write(Q.X); w.Write(Q.Y); w.Write(Q.Z); w.Write(Q.W);
                    break;
            }
        }

        /// <summary>Reads a value of the given field definition from the wire.</summary>
        public static FieldValue Read(BinaryReader r, FieldDef def)
        {
            switch (def.Type)
            {
                case FieldType.Bool: return Number(r.ReadBoolean() ? 1 : 0);
                case FieldType.Byte: return Number(r.ReadByte());
                case FieldType.Int: return Number(r.ReadInt32());
                case FieldType.UInt: return Number(r.ReadUInt32());
                case FieldType.Long: return Number(r.ReadInt64());
                case FieldType.Double: return Number(r.ReadDouble());
                case FieldType.String: return String(r.ReadString());
                case FieldType.Float: return Number(ReadScalar(r, def.Precision));
                case FieldType.Vector2:
                    return Vector2(new Vec2(ReadScalar(r, def.Precision), ReadScalar(r, def.Precision)));
                case FieldType.Vector3:
                    return Vector3(new Vec3(ReadScalar(r, def.Precision), ReadScalar(r, def.Precision), ReadScalar(r, def.Precision)));
                case FieldType.Quaternion:
                    return Quaternion(new Quat(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                default: return new FieldValue();
            }
        }

        // Quantized floats ride as scaled int32 (value/precision, rounded); raw otherwise.
        private static void WriteScalar(BinaryWriter w, float v, float precision)
        {
            if (precision > 0) w.Write((int)Math.Round(v / precision));
            else w.Write(v);
        }

        private static float ReadScalar(BinaryReader r, float precision)
            => precision > 0 ? r.ReadInt32() * precision : r.ReadSingle();

        /// <summary>Interpolates between two samples of the same field type, or snaps to <paramref name="from"/> for discrete types.</summary>
        public static FieldValue Interpolate(FieldValue from, FieldValue to, float t, FieldType type)
        {
            switch (type)
            {
                case FieldType.Float:
                case FieldType.Double:
                    return Number(from.Num + (to.Num - from.Num) * t);
                case FieldType.Vector2:
                case FieldType.Vector3:
                    return Vector3(Vec3.Lerp(from.V3, to.V3, t));
                case FieldType.Quaternion:
                    return Quaternion(Quat.Nlerp(from.Q, to.Q, t));
                default:
                    return from;   // discrete fields hold the previous sample until it's replaced
            }
        }

        /// <inheritdoc/>
        public bool Equals(FieldValue other)
            => Num.Equals(other.Num)
               && V3.X == other.V3.X && V3.Y == other.V3.Y && V3.Z == other.V3.Z
               && Q.X == other.Q.X && Q.Y == other.Q.Y && Q.Z == other.Q.Z && Q.W == other.Q.W
               && string.Equals(Str, other.Str, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is FieldValue v && Equals(v);

        /// <inheritdoc/>
        public override int GetHashCode() => Num.GetHashCode() ^ (Str?.GetHashCode() ?? 0);
    }
}
