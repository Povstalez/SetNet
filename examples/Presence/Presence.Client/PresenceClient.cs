using SetNet.Config;
using SetNet.Core;

namespace Presence.Client;

/// <summary>Minimal client for the presence/pub-sub demo.</summary>
public sealed class PresenceClient : BaseClient
{
    /// <summary>Creates the client with the given configuration.</summary>
    public PresenceClient(Configuration config) : base(config) { }
    /// <inheritdoc/>
    protected override void OnConnected() { }
    /// <inheritdoc/>
    protected override void OnDisconnected() { }
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[client] error: {error}");
}
