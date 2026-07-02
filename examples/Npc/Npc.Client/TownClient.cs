using SetNet.Config;
using SetNet.Core;

namespace Npc.Client;

/// <summary>A minimal client; all NPC behaviour comes from <c>client.UseNpc()</c> in <c>Program.cs</c>.</summary>
public sealed class TownClient : BaseClient
{
    public TownClient(Configuration config) : base(config) { }
    protected override void OnConnected() => Console.WriteLine("[client] connected");
    protected override void OnDisconnected() => Console.WriteLine("[client] disconnected");
    protected override void OnError(string error) => Console.WriteLine($"[client] error: {error}");
}
