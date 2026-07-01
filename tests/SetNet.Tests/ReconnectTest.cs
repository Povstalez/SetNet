using SetNet.Config;
using SetNet.Core;
using SetNet.Tests.Data;

namespace SetNet.Tests;

public class ReconnectTestClient : BaseClient
{
    public ReconnectTestClient(Configuration config) : base(config)
    {
    }

    public void DisconnectIntentionally()
    {
        Disconnect();
    }

    protected override void OnConnected()
    {
        Console.WriteLine("[Client] Connected to server");
    }

    protected override void OnDisconnected()
    {
        Console.WriteLine("[Client] OnDisconnected called - connection fully closed");
    }

    protected override void OnError(string error)
    {
        Console.WriteLine($"[Client] OnError: {error}");
    }

    protected override void OnUnexpectedDisconnect()
    {
        Console.WriteLine("[Client] OnUnexpectedDisconnect called - server dropped connection");
    }

    protected override void OnReconnecting(int attempt, int maxAttempts)
    {
        Console.WriteLine($"[Client] OnReconnecting - attempt {attempt}/{maxAttempts}");
    }

    protected override void OnReconnected()
    {
        Console.WriteLine("[Client] OnReconnected - successfully reconnected!");
    }

    protected override void OnReconnectFailed()
    {
        Console.WriteLine("[Client] OnReconnectFailed - all reconnect attempts exhausted");
    }
}
