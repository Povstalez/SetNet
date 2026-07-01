using System;

namespace SetNet.Logging
{
    /// <summary>
    /// Severity classification for log messages, ordered from least to most critical. Used by
    /// <see cref="ILogger"/> implementations to categorize and (optionally) filter or format output.
    /// </summary>
    public enum LogLevel
    {
        /// <summary>Verbose diagnostic detail useful only while debugging; typically suppressed in production.</summary>
        Debug,

        /// <summary>Normal informational messages about expected operation and lifecycle events.</summary>
        Info,

        /// <summary>A recoverable or unexpected condition that does not stop operation but warrants attention.</summary>
        Warning,

        /// <summary>A failure or error condition that prevented an operation from completing as intended.</summary>
        Error
    }

    /// <summary>
    /// Pluggable logging abstraction for the library, letting host applications route SetNet's diagnostic
    /// output to their own logging framework. Components accept an <see cref="ILogger"/> instead of writing to
    /// the console directly, keeping the networking core decoupled from any specific logging implementation.
    /// </summary>
    public interface ILogger
    {
        /// <summary>
        /// Records a single log message at the specified severity.
        /// </summary>
        /// <param name="message">The text to log.</param>
        /// <param name="level">The severity of the message; defaults to <see cref="LogLevel.Info"/>.</param>
        void Log(string message, LogLevel level = LogLevel.Info);
    }

    /// <summary>
    /// Default <see cref="ILogger"/> that writes each message to standard output, prefixed with its severity.
    /// A convenient zero-configuration sink for samples, tests, and simple applications.
    /// </summary>
    public class ConsoleLogger : ILogger
    {
        /// <summary>
        /// Writes the message to the console as <c>[Level] message</c>.
        /// </summary>
        /// <param name="message">The text to log.</param>
        /// <param name="level">The severity used as the line prefix; defaults to <see cref="LogLevel.Info"/>.</param>
        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            Console.WriteLine($"[{level}] {message}");
        }
    }

    /// <summary>
    /// An <see cref="ILogger"/> that discards every message. Used as the default when no logger is supplied, so
    /// components can log unconditionally without null checks and without producing any output.
    /// </summary>
    public class NoOpLogger : ILogger
    {
        /// <summary>
        /// Intentionally does nothing; both parameters are ignored.
        /// </summary>
        /// <param name="message">The text that would be logged (ignored).</param>
        /// <param name="level">The severity that would apply (ignored); defaults to <see cref="LogLevel.Info"/>.</param>
        public void Log(string message, LogLevel level = LogLevel.Info) { }
    }
}
