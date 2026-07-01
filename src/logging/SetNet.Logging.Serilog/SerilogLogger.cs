using System;
using Serilog;
using Serilog.Events;
using SetNet.Logging;

namespace SetNet.Logging.Serilog
{
    /// <summary>
    /// A SetNet <see cref="ILogger"/> backed by Serilog, so the library's diagnostics flow into your Serilog
    /// pipeline (console, file, Seq, …). Assign it via <c>Configuration.Logger</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
    /// var config = new Configuration { Logger = new SerilogLogger() };
    /// </code>
    /// </example>
    public sealed class SerilogLogger : ILogger
    {
        private readonly global::Serilog.ILogger _logger;

        /// <summary>Uses the given Serilog logger (defaults to the static <see cref="Log.Logger"/>).</summary>
        /// <param name="logger">The Serilog logger to write to; null uses <see cref="Log.Logger"/>.</param>
        public SerilogLogger(global::Serilog.ILogger? logger = null)
            => _logger = logger ?? global::Serilog.Log.Logger;

        /// <inheritdoc/>
        public void Log(string message, LogLevel level = LogLevel.Info)
            => _logger.Write(Map(level), "{Message}", message);

        private static LogEventLevel Map(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return LogEventLevel.Debug;
                case LogLevel.Warning: return LogEventLevel.Warning;
                case LogLevel.Error: return LogEventLevel.Error;
                default: return LogEventLevel.Information;
            }
        }
    }
}
