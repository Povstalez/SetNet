using System;

namespace SetNet.Core.Commands
{
    /// <summary>
    /// The single seam used to construct every reflection-discovered component during startup discovery: message handlers
    /// (<c>[MessageHandler]</c>), unified-protocol channel services and <c>[Op]</c> router classes (<c>[ProtocolChannel]</c>),
    /// client <c>[Event]</c> push handlers, and <c>[RpcMethod]</c> RPC handlers. By default each is created with its public
    /// parameterless constructor (<see cref="Activator"/>). Set <see cref="Factory"/> to route construction through a
    /// container instead — this is how <c>SetNet.DependencyInjection</c> lets all of them receive injected dependencies via
    /// their constructors. A factory that returns <c>null</c> for a type falls back to the default construction.
    /// </summary>
    public static class HandlerActivator
    {
        /// <summary>Optional factory that builds an instance for a given discovered type (return null to fall back to the default).</summary>
        public static Func<Type, object?>? Factory;

        /// <summary>Creates an instance via <see cref="Factory"/> when set (and non-null), otherwise the parameterless constructor.</summary>
        public static object Create(Type handlerType)
            => Factory?.Invoke(handlerType) ?? Activator.CreateInstance(handlerType)!;
    }
}
