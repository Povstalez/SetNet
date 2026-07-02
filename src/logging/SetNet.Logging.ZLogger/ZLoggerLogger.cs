using ZLogger;

namespace SetNet.Logging
{
    /// <summary>
    /// A <see cref="ILogger"/> that routes SetNet's diagnostics into <c>ZLogger</c> via its zero-allocation
    /// <c>ZLog*</c> API. Construct it around a <c>Microsoft.Extensions.Logging.ILogger</c> obtained from a ZLogger-configured
    /// <c>ILoggerFactory</c>, and set it on your configuration (<c>config.Logger = new ZLoggerLogger(msLogger)</c>).
    /// Level map: Debug→Debug, Info→Information, Warning→Warning, Error→Error.
    /// </summary>
    public sealed class ZLoggerLogger : ILogger
    {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;

        /// <summary>Creates the adapter around a ZLogger-backed <c>Microsoft.Extensions.Logging.ILogger</c>.</summary>
        public ZLoggerLogger(Microsoft.Extensions.Logging.ILogger logger)
            => _logger = logger ?? throw new System.ArgumentNullException(nameof(logger));

        /// <inheritdoc/>
        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            switch (level)
            {
                case LogLevel.Debug: _logger.ZLogDebug($"{message}"); break;
                case LogLevel.Warning: _logger.ZLogWarning($"{message}"); break;
                case LogLevel.Error: _logger.ZLogError($"{message}"); break;
                default: _logger.ZLogInformation($"{message}"); break;
            }
        }
    }
}
