using System;

namespace SetNet.Logging
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    public interface ILogger
    {
        void Log(string message, LogLevel level = LogLevel.Info);
    }

    public class ConsoleLogger : ILogger
    {
        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            Console.WriteLine($"[{level}] {message}");
        }
    }

    public class NoOpLogger : ILogger
    {
        public void Log(string message, LogLevel level = LogLevel.Info) { }
    }
}
