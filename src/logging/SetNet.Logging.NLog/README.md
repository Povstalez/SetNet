<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Logging.NLog

**Route [SetNet](https://www.nuget.org/packages/SetNet)'s internal diagnostics into [NLog](https://nlog-project.org/).**

SetNet logs through a tiny pluggable `ILogger` seam (`Configuration.Logger`). This adapter forwards every SetNet log line — accept/connect churn, dropped handshakes, heartbeat timeouts, handler exceptions — into your NLog pipeline so it lands in the same targets, layouts, and rules as the rest of your app.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Logging.NLog
```

## Usage

```csharp
using SetNet.Config;
using SetNet.Logging;

var config = new Configuration
{
    Host = "0.0.0.0",
    Port = 5000,
    Logger = new NLogLogger(),   // uses LogManager.GetLogger("SetNet")
};
```

Pass a specific NLog logger if you want a custom name/rules:

```csharp
config.Logger = new NLogLogger(NLog.LogManager.GetLogger("MyGame.Net"));
```

Configure NLog itself as usual (`NLog.config`, `nlog.config`, or fluent `LogManager.Setup()`); this package only bridges SetNet's messages into it.

## Level mapping

| SetNet `LogLevel` | NLog level |
|---|---|
| `Debug` | `Debug` |
| `Info` | `Info` |
| `Warning` | `Warn` |
| `Error` | `Error` |

## Notes

- **Bridge only.** This package doesn't configure NLog or add targets — set NLog up however you already do; SetNet just writes into it.
- **Default logger name is `"SetNet"`.** Add a rule/filter on that name to isolate or mute framework noise.
- **`ILogger` is the seam.** Anything that implements SetNet's `ILogger` works here; this is just the ready-made NLog implementation. See also [`SetNet.Logging.Serilog`](https://www.nuget.org/packages/SetNet.Logging.Serilog) and [`SetNet.Logging.ZLogger`](https://www.nuget.org/packages/SetNet.Logging.ZLogger).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
