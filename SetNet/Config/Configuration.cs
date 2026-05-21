using SetNet.Logging;

namespace SetNet.Config
{
    public class Configuration
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public int BufferSize { get; set; } = 4096;
        public int MaxConnections { get; set; } = 100;
        public bool UseSsl { get; set; } = false;

        public bool AutoReconnect { get; set; } = false;
        public int MaxReconnectAttempts { get; set; } = 3;
        public int ReconnectDelayMs { get; set; } = 1000;

        public int ConnectTimeoutMs { get; set; } = 10000;

        public bool HeartbeatEnabled { get; set; } = false;
        public int HeartbeatIntervalMs { get; set; } = 5000;
        public int HeartbeatTimeoutMs { get; set; } = 15000;

        public ILogger Logger { get; set; } = new ConsoleLogger();
    }
}