using SetNet.Config;
using SetNet.Core;

namespace Economy.Client;

/// <summary>Minimal client for the economy/world demo.</summary>
public sealed class GameClient : BaseClient
{
    /// <summary>Creates the client with the given configuration.</summary>
    public GameClient(Configuration config) : base(config) { }
    /// <inheritdoc/>
    protected override void OnConnected() { }
    /// <inheritdoc/>
    protected override void OnDisconnected() { }
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[client] error: {error}");
}
