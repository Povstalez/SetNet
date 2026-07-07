using System;
using System.Collections.Generic;
using SetNet.Core.Transport;

namespace SetNet.Config
{
    /// <summary>Severity for configuration analysis results.</summary>
    public enum ConfigurationIssueSeverity
    {
        /// <summary>An advisory that may be acceptable for local development but should be reviewed.</summary>
        Warning,

        /// <summary>A setting combination that is unsafe or internally inconsistent for production.</summary>
        Error
    }

    /// <summary>A production-readiness finding for a <see cref="Configuration"/>.</summary>
    public sealed class ConfigurationIssue
    {
        /// <summary>Creates a configuration issue.</summary>
        public ConfigurationIssue(ConfigurationIssueSeverity severity, string message)
        {
            Severity = severity;
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        /// <summary>Issue severity.</summary>
        public ConfigurationIssueSeverity Severity { get; }

        /// <summary>Human-readable issue text.</summary>
        public string Message { get; }

        /// <inheritdoc/>
        public override string ToString() => $"{Severity}: {Message}";
    }

    /// <summary>Advisory checks for configuration profiles and production readiness.</summary>
    public static class ConfigurationAnalyzer
    {
        /// <summary>Analyzes a configuration and returns warnings/errors without mutating it.</summary>
        public static ConfigurationIssue[] Analyze(Configuration config, bool production = false)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var issues = new List<ConfigurationIssue>();

            if (config.Runtime == null)
                issues.Add(Error("Runtime is not configured."));
            if (string.IsNullOrWhiteSpace(config.Host))
                issues.Add(Error("Host is empty."));
            if (config.Port < 1 || config.Port > 65535)
                issues.Add(Error("Port must be in 1..65535."));
            if (config.MaxMessageSize == 0)
                issues.Add(Warning("MaxMessageSize is disabled; oversized TCP frames can grow memory without a hard cap."));
            if (config.MaxInboundQueue == 0)
                issues.Add(Warning("MaxInboundQueue is disabled; inbound buffering can grow under a fast sender."));
            if (config.MaxInFlightMessages == 0 && !config.SequentialDispatch)
                issues.Add(Warning("Handler concurrency is unbounded and dispatch order is not guaranteed."));
            if (!config.HeartbeatEnabled)
                issues.Add(Warning("Heartbeat is disabled; silent disconnects may take a long time to detect."));
            if (config.TransportType == TransportType.Udp && !config.UdpReliabilityEnabled && config.DefaultDelivery == DeliveryMethod.Reliable)
                issues.Add(Error("UDP transport defaults to reliable delivery while UDP reliability is disabled."));

            if (production)
            {
                if (!config.UseSsl && (config.TransportType == TransportType.Tcp || config.TransportType == TransportType.Both))
                    issues.Add(Warning("TLS is disabled on the TCP channel; use TLS for public or authenticated traffic."));
                if (config.MaxConnectionsPerIpPerSecond <= 0)
                    issues.Add(Warning("Per-IP connection rate limiting is disabled."));
                if (config.TransportType != TransportType.Tcp && config.DefaultDelivery == DeliveryMethod.Unreliable)
                    issues.Add(Warning("DefaultDelivery is Unreliable; critical application messages must opt into Reliable."));
                if (config.SendTimeoutMs <= 0)
                    issues.Add(Warning("SendTimeoutMs is disabled; a stuck TCP peer can hold the send lock indefinitely."));
            }

            return issues.ToArray();
        }

        private static ConfigurationIssue Warning(string message)
            => new ConfigurationIssue(ConfigurationIssueSeverity.Warning, message);

        private static ConfigurationIssue Error(string message)
            => new ConfigurationIssue(ConfigurationIssueSeverity.Error, message);
    }
}
