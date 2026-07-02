using System;

namespace SetNet.Protocol
{
    /// <summary>
    /// Thrown on the calling side when a unified-protocol request fails: the server-side channel handler threw (its
    /// message is relayed here), or no channel service is registered for the requested channel/op.
    /// </summary>
    public class ProtocolException : Exception
    {
        /// <summary>Creates a <see cref="ProtocolException"/> with the given message (usually the server-side error text).</summary>
        /// <param name="message">The error text.</param>
        public ProtocolException(string message) : base(message) { }
    }
}
