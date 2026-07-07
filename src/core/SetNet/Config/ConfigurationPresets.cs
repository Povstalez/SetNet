using System;
using SetNet.Core.Transport;

namespace SetNet.Config
{
    /// <summary>Factory methods for common configuration profiles.</summary>
    public static class ConfigurationPresets
    {
        /// <summary>Local development TCP profile with fast feedback and modest limits.</summary>
        public static Configuration Development(string host = "127.0.0.1", int port = 5000)
            => new Configuration
            {
                Host = host,
                Port = port,
                TransportType = TransportType.Tcp,
                HeartbeatEnabled = true,
                HeartbeatIntervalMs = 5000,
                HeartbeatTimeoutMs = 15000,
                MaxInFlightMessages = 256,
                MaxConnectionsPerIpPerSecond = 0
            };

        /// <summary>Public TCP server profile with TLS expected and bounded dispatch/back-pressure defaults.</summary>
        public static Configuration ProductionTcp(string host, int port, global::SetNet.SetNetRuntime? runtime = null)
            => new Configuration
            {
                Host = host ?? throw new ArgumentNullException(nameof(host)),
                Port = port,
                Runtime = runtime ?? global::SetNet.SetNetRuntime.Default,
                TransportType = TransportType.Tcp,
                UseSsl = true,
                HeartbeatEnabled = true,
                HeartbeatIntervalMs = 5000,
                HeartbeatTimeoutMs = 15000,
                MaxConnectionsPerIpPerSecond = 20,
                MaxInFlightMessages = 512,
                MaxInboundQueue = 8192,
                MaxMessageSize = 1024 * 1024,
                SendTimeoutMs = 30000
            };

        /// <summary>Realtime game profile using Both mode: reliable traffic on TCP, unreliable snapshots on UDP.</summary>
        public static Configuration RealtimeGame(string host, int port, global::SetNet.SetNetRuntime? runtime = null)
            => new Configuration
            {
                Host = host ?? throw new ArgumentNullException(nameof(host)),
                Port = port,
                Runtime = runtime ?? global::SetNet.SetNetRuntime.Default,
                TransportType = TransportType.Both,
                DefaultDelivery = DeliveryMethod.Reliable,
                HeartbeatEnabled = true,
                HeartbeatIntervalMs = 3000,
                HeartbeatTimeoutMs = 10000,
                MaxConnectionsPerIpPerSecond = 20,
                MaxInFlightMessages = 512,
                MaxInboundQueue = 8192,
                UdpMaxDatagramPayload = 1200,
                SendBatching = true,
                SendBatchFlushMs = 15
            };

        /// <summary>Security-oriented public server profile; combine with Auth/RateLimit modules as needed.</summary>
        public static Configuration SecurePublicServer(string host, int port, global::SetNet.SetNetRuntime? runtime = null)
        {
            var config = ProductionTcp(host, port, runtime);
            config.MaxConnectionsPerIpPerSecond = 10;
            config.MaxConnectionsLimit = 1000;
            config.MaxInboundQueue = 4096;
            config.MaxInFlightMessages = 256;
            return config;
        }
    }
}
