using System;

namespace SetNet.Rpc
{
    /// <summary>
    /// Marks an <see cref="IRpcHandler{TRequest,TResponse}"/> implementation as the handler for a given RPC
    /// method id. Discovered by reflection at first use, exactly like SetNet's <c>[MessageHandler]</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RpcMethodAttribute : Attribute
    {
        /// <summary>The RPC method id this handler serves; the caller passes the same id to <c>CallAsync</c>.</summary>
        public ushort MethodId { get; }

        /// <summary>Associates the decorated handler with an RPC method id.</summary>
        /// <param name="methodId">The method id callers use to invoke this handler.</param>
        public RpcMethodAttribute(ushort methodId) => MethodId = methodId;
    }
}
