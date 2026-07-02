using SetNet.Config;
using SetNet.Core;

namespace FileTransfer.Client;

/// <summary>Minimal client for the file-transfer demo (all traffic is the SetNet.Streams upload).</summary>
public sealed class FileClient : BaseClient
{
    /// <summary>Creates the client with the given configuration.</summary>
    public FileClient(Configuration config) : base(config) { }
    /// <inheritdoc/>
    protected override void OnConnected() { }
    /// <inheritdoc/>
    protected override void OnDisconnected() { }
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[client] error: {error}");
}
