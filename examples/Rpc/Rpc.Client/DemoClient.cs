using SetNet.Config;
using SetNet.Core;

namespace Rpc.Client;

/// <summary>Minimal client for the RPC demo (all traffic is request/reply via <c>CallAsync</c>).</summary>
public sealed class DemoClient : BaseClient
{
    /// <summary>Creates the client with the given configuration.</summary>
    public DemoClient(Configuration config) : base(config) { }
    /// <inheritdoc/>
    protected override void OnConnected() { }
    /// <inheritdoc/>
    protected override void OnDisconnected() { }
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[client] error: {error}");
}
