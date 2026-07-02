using SetNet.Config;
using SetNet.Core;

namespace StateSync.Client;

/// <summary>A minimal client; replication comes from <c>client.UseStateSync()</c> in <c>Program.cs</c>.</summary>
public sealed class BallClient : BaseClient
{
    public BallClient(Configuration config) : base(config) { }
    protected override void OnConnected() => Console.WriteLine("[client] connected");
    protected override void OnDisconnected() => Console.WriteLine("[client] disconnected");
    protected override void OnError(string error) => Console.WriteLine($"[client] error: {error}");
}
