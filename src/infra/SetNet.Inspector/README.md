# SetNet.Inspector

**Built-in diagnostics dashboard for [SetNet](https://www.nuget.org/packages/SetNet).**

A tiny `HttpListener` endpoint (no ASP.NET dependency) that serves live `NetworkMetrics` and the active connection count as JSON at `/metrics`, plus a self-refreshing HTML page at `/`. Point a browser at it to watch traffic in real time, or scrape `/metrics` from your tooling.

```csharp
using SetNet.Inspector;

var inspector = new InspectorServer(config, server, port: 9090);
inspector.Start();
// browse http://localhost:9090/  ·  scrape http://localhost:9090/metrics
```

Bind to `localhost` (default) and keep it off the public internet, or put auth in front — it exposes internal counters.

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
