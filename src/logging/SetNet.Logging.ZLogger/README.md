<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Logging.ZLogger

**Route [SetNet](https://www.nuget.org/packages/SetNet)'s internal diagnostics into [ZLogger](https://github.com/Cysharp/ZLogger).**

ZLogger is a zero-allocation, structured logging provider built on `Microsoft.Extensions.Logging`. This adapter forwards SetNet's log lines into a `Microsoft.Extensions.Logging.ILogger`, so with a ZLogger provider configured they flow through ZLogger's high-throughput pipeline — ideal for game servers and hot paths where GC pressure matters.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Logging.ZLogger
```

## Usage

```csharp
using Microsoft.Extensions.Logging;
using SetNet.Config;
using SetNet.Logging;
using ZLogger;

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.SetMinimumLevel(LogLevel.Debug);
    b.AddZLoggerConsole();          // or AddZLoggerFile / AddZLoggerRollingFile ...
});

var config = new Configuration
{
    Host = "0.0.0.0",
    Port = 5000,
    Logger = new ZLoggerLogger(loggerFactory.CreateLogger("SetNet")),
};
```

## Level mapping

| SetNet `LogLevel` | ZLogger call |
|---|---|
| `Debug` | `ZLogDebug` |
| `Info` | `ZLogInformation` |
| `Warning` | `ZLogWarning` |
| `Error` | `ZLogError` |

## Notes

- **Bridge only.** You own the `LoggerFactory`/provider setup; this package just adapts SetNet's `ILogger` onto a `Microsoft.Extensions.Logging.ILogger`.
- **Wants a ZLogger provider.** It works with any `Microsoft.Extensions.Logging` backend, but you get ZLogger's zero-alloc benefit only when a ZLogger provider is added.
- **Alternatives.** See [`SetNet.Logging.Serilog`](https://www.nuget.org/packages/SetNet.Logging.Serilog) and [`SetNet.Logging.NLog`](https://www.nuget.org/packages/SetNet.Logging.NLog).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
