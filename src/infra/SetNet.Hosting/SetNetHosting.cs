using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SetNet.Core;

namespace SetNet.Hosting
{
    /// <summary>
    /// An <see cref="IHostedService"/> that starts a SetNet server when the .NET Generic Host / ASP.NET Core app starts
    /// and stops it on shutdown — so your game/realtime server participates in the standard host lifecycle (graceful
    /// shutdown, DI, configuration, logging).
    /// </summary>
    public sealed class SetNetServerHostedService : IHostedService
    {
        private readonly BaseServer _server;

        /// <summary>Creates the hosted service around a server instance (typically resolved from DI).</summary>
        public SetNetServerHostedService(BaseServer server) => _server = server ?? throw new ArgumentNullException(nameof(server));

        /// <inheritdoc/>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = _server.StartAsync();   // runs the accept loop until StopAsync; don't block host startup
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task StopAsync(CancellationToken cancellationToken) => _server.StopAsync();
    }

    /// <summary>Host-builder helpers for running a SetNet server as a hosted service.</summary>
    public static class SetNetHostingExtensions
    {
        /// <summary>
        /// Registers a SetNet server (built by <paramref name="serverFactory"/>) as a singleton and runs it as a hosted
        /// service for the app's lifetime.
        /// </summary>
        public static IServiceCollection AddSetNetServer(this IServiceCollection services, Func<IServiceProvider, BaseServer> serverFactory)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (serverFactory == null) throw new ArgumentNullException(nameof(serverFactory));
            services.AddSingleton(serverFactory);
            services.AddHostedService(sp => new SetNetServerHostedService(sp.GetRequiredService<BaseServer>()));
            return services;
        }
    }
}
