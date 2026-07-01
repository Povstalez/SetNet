using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SetNet.Core;

namespace SetNet.HealthChecks
{
    /// <summary>
    /// An ASP.NET Core / .NET <see cref="IHealthCheck"/> reporting a SetNet server's liveness and its active connection
    /// count — so orchestrators (Kubernetes, load balancers, monitoring) can probe the realtime server through the standard
    /// health-check pipeline. Optionally reports Degraded once the connection count crosses a warning threshold.
    /// </summary>
    public sealed class SetNetHealthCheck : IHealthCheck
    {
        private readonly BaseServer _server;
        private readonly int _degradedAtConnections;

        /// <summary>Creates the health check for a server, optionally flagging Degraded at/above a connection count (0 = never).</summary>
        public SetNetHealthCheck(BaseServer server, int degradedAtConnections = 0)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _degradedAtConnections = degradedAtConnections;
        }

        /// <inheritdoc/>
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var active = _server.ActiveConnections;
            var data = new Dictionary<string, object> { ["activeConnections"] = active };

            if (_degradedAtConnections > 0 && active >= _degradedAtConnections)
                return Task.FromResult(HealthCheckResult.Degraded($"High load: {active} active connections.", data: data));

            return Task.FromResult(HealthCheckResult.Healthy($"SetNet server healthy ({active} active connections).", data));
        }
    }

    /// <summary>Registration helper for the SetNet health check.</summary>
    public static class SetNetHealthCheckExtensions
    {
        /// <summary>Adds the SetNet server health check to the health-checks builder.</summary>
        public static IHealthChecksBuilder AddSetNet(this IHealthChecksBuilder builder, BaseServer server, string name = "setnet", int degradedAtConnections = 0)
            => builder.AddCheck(name, new SetNetHealthCheck(server, degradedAtConnections));
    }
}
