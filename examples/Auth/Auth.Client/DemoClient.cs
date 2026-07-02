using SetNet.Config;
using SetNet.Core;

namespace Auth.Client;

/// <summary>Minimal client for the auth demo (the SetNet.Auth driver authenticates automatically on connect).</summary>
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
