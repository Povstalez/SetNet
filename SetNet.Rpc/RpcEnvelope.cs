using MessagePack;

namespace SetNet.Rpc
{
    /// <summary>
    /// The wire envelope for one RPC request or response: a correlation id (to match a response to its request),
    /// the method id, an error flag, and the serialized body. The <b>body</b> is the request/response object
    /// serialized with the app's configured <see cref="SetNet.Messaging.SetNetSerializer"/>; only this thin
    /// envelope is fixed by the RPC layer.
    /// </summary>
    /// <remarks>
    /// Carries both MessagePack attributes (so it works with the MessagePack serializer) and plain public
    /// properties (so it works with System.Text.Json or any reflection-based serializer). Both ends of the call
    /// use the same serializer, as with any SetNet message.
    /// </remarks>
    [MessagePackObject]
    public class RpcEnvelope
    {
        /// <summary>Process-unique id linking a response back to the awaiting request.</summary>
        [Key(0)] public int CorrelationId { get; set; }

        /// <summary>The RPC method id (matches an <see cref="RpcMethodAttribute"/> on the server side).</summary>
        [Key(1)] public ushort MethodId { get; set; }

        /// <summary>True when <see cref="Body"/> carries a UTF-8 error message instead of a serialized response.</summary>
        [Key(2)] public bool IsError { get; set; }

        /// <summary>The serialized request/response body (or, when <see cref="IsError"/>, a UTF-8 error string).</summary>
        [Key(3)] public byte[] Body { get; set; } = System.Array.Empty<byte>();
    }
}
