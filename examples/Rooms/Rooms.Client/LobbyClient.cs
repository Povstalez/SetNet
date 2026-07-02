using SetNet.Config;
using SetNet.Core;

namespace Rooms.Client;

/// <summary>A minimal client; all room behaviour comes from <c>client.UseRooms()</c> in <c>Program.cs</c>.</summary>
public sealed class LobbyClient : BaseClient
{
    public LobbyClient(Configuration config) : base(config) { }
    protected override void OnConnected() => Console.WriteLine("[client] connected");
    protected override void OnDisconnected() => Console.WriteLine("[client] disconnected");
    protected override void OnError(string error) => Console.WriteLine($"[client] error: {error}");
}
