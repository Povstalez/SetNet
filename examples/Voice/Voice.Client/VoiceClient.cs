using SetNet.Config;
using SetNet.Core;

namespace Voice.Client;

/// <summary>Minimal client for the voice demo (all traffic is the SetNet.Voice channel).</summary>
public sealed class VoiceRelayClient : BaseClient
{
    /// <summary>Creates the client with the given configuration.</summary>
    public VoiceRelayClient(Configuration config) : base(config) { }
    /// <inheritdoc/>
    protected override void OnConnected() { }
    /// <inheritdoc/>
    protected override void OnDisconnected() { }
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[client] error: {error}");
}
