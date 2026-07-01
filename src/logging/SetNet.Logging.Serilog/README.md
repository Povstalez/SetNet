<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Logging.Serilog

**Serilog logging for [SetNet](https://www.nuget.org/packages/SetNet).**

Routes SetNet's internal diagnostics (connection errors, reconnects, handler faults, …) into your Serilog pipeline.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Logging.Serilog
```

## Usage

```csharp
using Serilog;
using SetNet.Logging.Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

var config = new Configuration
{
    Host = "0.0.0.0", Port = 5000,
    Logger = new SerilogLogger()          // uses Log.Logger; or pass a specific ILogger
};
```

SetNet's `LogLevel` maps to Serilog: `Debug→Debug`, `Info→Information`, `Warning→Warning`, `Error→Error`.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet

## License

MIT
