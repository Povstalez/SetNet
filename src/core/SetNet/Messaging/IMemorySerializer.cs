using System;

namespace SetNet.Messaging
{
    /// <summary>
    /// Optional companion to <see cref="ISerializer"/> for serializers that can decode straight from a slice of a
    /// larger buffer, without that slice first being copied into an array of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implement it alongside <see cref="ISerializer"/> whenever the underlying format supports it (MessagePack,
    /// protobuf and System.Text.Json all do). SetNet uses it on the client push-event path: an event body arrives
    /// inside the received frame, and with this interface it is deserialized in place instead of being copied out
    /// first. In a request/response workload the copy is noise; in a game client taking hundreds of push events
    /// per frame it is a steady stream of short-lived arrays for the collector to sweep.
    /// </para>
    /// <para>
    /// Purely additive: a serializer that does not implement it keeps working through <see cref="ISerializer"/>,
    /// copy and all. Implementations must be thread-safe, like <see cref="ISerializer"/> itself.
    /// </para>
    /// <para>
    /// The memory passed in is only valid for the duration of the call — it is a window onto a buffer the caller
    /// still owns. Decode from it; do not store it.
    /// </para>
    /// </remarks>
    public interface IMemorySerializer
    {
        /// <summary>Deserializes a message of type <typeparamref name="T"/> from a payload slice.</summary>
        /// <typeparam name="T">The target message type to reconstruct.</typeparam>
        /// <param name="data">The payload window; valid only until this call returns.</param>
        /// <returns>The decoded message.</returns>
        T Deserialize<T>(ReadOnlyMemory<byte> data);
    }
}
