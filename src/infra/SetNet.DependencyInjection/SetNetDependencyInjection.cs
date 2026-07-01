using System;
using Microsoft.Extensions.DependencyInjection;
using SetNet.Core.Commands;

namespace SetNet.DependencyInjection
{
    /// <summary>
    /// Bridges SetNet's message-handler construction to a <see cref="IServiceProvider"/>, so your <c>[MessageHandler]</c>
    /// classes can receive **injected dependencies** (services, loggers, repositories) via their constructors instead of
    /// being new'd with a parameterless ctor. Call <see cref="UseSetNetHandlers"/> once at startup **before** constructing
    /// your <c>BaseClient</c>/<c>BaseServer</c> (handlers are discovered and built when the executor is created).
    /// </summary>
    public static class SetNetDependencyInjection
    {
        /// <summary>
        /// Routes handler construction through the container: a registered handler type is resolved from the provider;
        /// an unregistered one is still built with constructor injection via <see cref="ActivatorUtilities"/>.
        /// </summary>
        /// <param name="provider">The application's service provider.</param>
        /// <returns>The same provider, for chaining.</returns>
        public static IServiceProvider UseSetNetHandlers(this IServiceProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            HandlerActivator.Factory = type => provider.GetService(type) ?? ActivatorUtilities.CreateInstance(provider, type);
            return provider;
        }
    }
}
