using SetNet.Config;
using SetNet.Core;

namespace Trade.Client;

/// <summary>Minimal client for the trade demo (all trade traffic goes through the SetNet.Trade driver).</summary>
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
