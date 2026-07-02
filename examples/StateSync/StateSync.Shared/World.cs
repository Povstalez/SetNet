using SetNet.StateSync;

namespace StateSync.Shared;

/// <summary>
/// The replicated world's schema, registered identically on the server and every client. Each "ball" archetype has a
/// 3D position (interpolated + quantized to centimetres) and a hue (interpolated) so clients can colour it. Field
/// indices are shared so both ends read the same slots.
/// </summary>
public static class World
{
    /// <summary>Archetype id for a bouncing ball.</summary>
    public const ushort Ball = 1;

    /// <summary>Field 0: position (Vector3).</summary>
    public const int Position = 0;

    /// <summary>Field 1: hue 0..1 (Float).</summary>
    public const int Hue = 1;

    /// <summary>Registers the world schema. Call once at startup on both the server and the client.</summary>
    public static void Register()
    {
        ReplicaRegistry.Register(ReplicaSchema.Create(Ball)
            .Field(FieldType.Vector3, interpolate: true, precision: 0.01f)   // 0: position
            .Field(FieldType.Float, interpolate: true)                       // 1: hue
            .Build());
    }
}
