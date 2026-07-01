using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;

namespace SetNet.Inspector
{
    /// <summary>
    /// A tiny built-in diagnostics dashboard for a SetNet server: it runs a lightweight HTTP endpoint (via
    /// <see cref="HttpListener"/>, no ASP.NET dependency) that serves live <see cref="NetworkMetrics"/> and the active
    /// connection count as JSON at <c>/metrics</c> plus a self-refreshing HTML page at <c>/</c>. Point a browser at it to
    /// watch traffic in real time, or scrape <c>/metrics</c> from your own tooling.
    /// </summary>
    public sealed class InspectorServer : IDisposable
    {
        private readonly Configuration _config;
        private readonly BaseServer _server;
        private readonly HttpListener _http = new HttpListener();
        private volatile bool _running;

        /// <summary>Creates the inspector for a server + its configuration (which owns the metrics). Call <see cref="Start"/> to begin serving.</summary>
        public InspectorServer(Configuration config, BaseServer server, int port = 9090, string host = "localhost")
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _server = server ?? throw new ArgumentNullException(nameof(server));
            var bind = string.IsNullOrEmpty(host) || host == "0.0.0.0" ? "+" : host;
            _http.Prefixes.Add($"http://{bind}:{port}/");
        }

        /// <summary>Starts serving the dashboard.</summary>
        public void Start()
        {
            _http.Start();
            _running = true;
            _ = AcceptLoop();
        }

        private async Task AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync().ConfigureAwait(false); }
                catch { return; }   // listener stopped

                try
                {
                    var path = ctx.Request.Url?.AbsolutePath ?? "/";
                    var (body, contentType) = path == "/metrics"
                        ? (MetricsJson(), "application/json")
                        : (Html(), "text/html; charset=utf-8");

                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentType = contentType;
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch { /* client went away */ }
            }
        }

        private string MetricsJson()
        {
            var m = _config.Metrics;
            return new StringBuilder()
                .Append('{')
                .Append("\"activeConnections\":").Append(_server.ActiveConnections).Append(',')
                .Append("\"messagesSent\":").Append(m.MessagesSent).Append(',')
                .Append("\"messagesReceived\":").Append(m.MessagesReceived).Append(',')
                .Append("\"connectionsAccepted\":").Append(m.ConnectionsAccepted).Append(',')
                .Append("\"connectionsRejected\":").Append(m.ConnectionsRejected).Append(',')
                .Append("\"reliableRetransmits\":").Append(m.ReliableRetransmits).Append(',')
                .Append("\"reliableAcksReceived\":").Append(m.ReliableAcksReceived).Append(',')
                .Append("\"handshakesDropped\":").Append(m.HandshakesDropped).Append(',')
                .Append("\"inboundDropped\":").Append(m.InboundDropped)
                .Append('}')
                .ToString();
        }

        private static string Html() => @"<!doctype html><html><head><meta charset=""utf-8""><title>SetNet Inspector</title>
<style>body{font-family:system-ui,sans-serif;margin:2rem;background:#0d1117;color:#c9d1d9}
h1{font-size:1.2rem}table{border-collapse:collapse}td{padding:.3rem .8rem;border-bottom:1px solid #21262d}
td.k{color:#8b949e}td.v{font-variant-numeric:tabular-nums;text-align:right}</style></head>
<body><h1>SetNet Inspector</h1><table id=t></table>
<script>
async function tick(){const r=await fetch('/metrics');const d=await r.json();
document.getElementById('t').innerHTML=Object.entries(d).map(([k,v])=>
`<tr><td class=k>${k}</td><td class=v>${v}</td></tr>`).join('');}
tick();setInterval(tick,1000);
</script></body></html>";

        /// <inheritdoc/>
        public void Dispose()
        {
            _running = false;
            try { _http.Close(); } catch { /* ignore */ }
        }
    }
}
