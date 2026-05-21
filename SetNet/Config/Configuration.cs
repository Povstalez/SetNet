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
    }
}