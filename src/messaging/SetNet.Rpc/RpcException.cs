using System;

namespace SetNet.Rpc
{
    /// <summary>
    /// Thrown on the calling side when an RPC fails: the server-side handler threw (the message is relayed here),
    /// or no handler is registered for the requested method id.
    /// </summary>
    public class RpcException : Exception
    {
        /// <summary>Creates an <see cref="RpcException"/> with the given message.</summary>
        /// <param name="message">The error text (usually the server-side handler's exception message).</param>
        public RpcException(string message) : base(message) { }
    }
}
