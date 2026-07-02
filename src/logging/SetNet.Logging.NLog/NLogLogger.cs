namespace SetNet.Logging
{
    /// <summary>
    /// A <see cref="ILogger"/> that routes SetNet's diagnostics into <c>NLog</c>. Set it once on your configuration
    /// (<c>config.Logger = new NLogLogger()</c>) and SetNet's internal log lines flow through your NLog targets/rules.
    /// Level map: Debug→Debug, Info→Info, Warning→Warn, Error→Error.
    /// </summary>
    public sealed class NLogLogger : ILogger
    {
        private readonly global::NLog.Logger _logger;

        /// <summary>Creates the adapter around an NLog logger (defaults to a logger named "SetNet").</summary>
        public NLogLogger(global::NLog.Logger? logger = null)
            => _logger = logger ?? global::NLog.LogManager.GetLogger("SetNet");

        /// <inheritdoc/>
        public void Log(string message, LogLevel level = LogLevel.Info) => _logger.Log(Map(level), message);

        private static global::NLog.LogLevel Map(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return global::NLog.LogLevel.Debug;
                case LogLevel.Warning: return global::NLog.LogLevel.Warn;
                case LogLevel.Error: return global::NLog.LogLevel.Error;
                default: return global::NLog.LogLevel.Info;
            }
        }
    }
}
