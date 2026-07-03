using System;
using Microsoft.Extensions.DependencyInjection;
using SetNet.Core.Commands;

namespace SetNet.DependencyInjection
{
    /// <summary>
    /// Bridges SetNet's component construction to a <see cref="IServiceProvider"/>, so **every reflection-discovered
    /// component** can receive **injected dependencies** via its constructor instead of being new'd with a parameterless
    /// ctor. This covers, through the one <see cref="HandlerActivator"/> seam:
    /// <list type="bullet">
    ///   <item><c>[MessageHandler]</c> message handlers (server &amp; client),</item>
    ///   <item><c>[ProtocolChannel]</c> channel services and <c>[Op]</c> router classes (the unified protocol),</item>
    ///   <item>client <c>[Event]</c> push handlers,</item>
    ///   <item><c>[RpcMethod]</c> RPC handlers (<c>SetNet.Rpc</c>).</item>
    /// </list>
    /// Call <see cref="UseSetNet"/> once at startup **before** constructing your <c>BaseClient</c>/<c>BaseServer</c> and
    /// before the first RPC/protocol use (components are discovered and built lazily on first use).
    /// </summary>
    public static class SetNetDependencyInjection
    {
        /// <summary>
        /// Routes construction of all discovered SetNet components through the container: a registered type is resolved
        /// from the provider; an unregistered one is still built with constructor injection via
        /// <see cref="ActivatorUtilities"/> (so its constructor parameters are satisfied from the container).
        /// </summary>
        /// <param name="provider">The application's service provider.</param>
        /// <returns>The same provider, for chaining.</returns>
        public static IServiceProvider UseSetNet(this IServiceProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            HandlerActivator.Factory = type => provider.GetService(type) ?? ActivatorUtilities.CreateInstance(provider, type);
            return provider;
        }

        /// <summary>Backwards-compatible alias of <see cref="UseSetNet"/> (the seam now also covers channel services, events and RPC, not only handlers).</summary>
        public static IServiceProvider UseSetNetHandlers(this IServiceProvider provider) => provider.UseSetNet();
    }
}
