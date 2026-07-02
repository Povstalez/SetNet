// Auth client entry point — demonstrates the SetNet.Auth enforced gate.
//   dotnet run --project examples/Auth/Auth.Client -- [token]
// Default token: "letmein" (the one the demo server accepts). Pass any other token to see auth FAIL.
//
// What this shows:
//   1. UseAuth(...) attaches BEFORE connecting; it authenticates automatically on connect (and every reconnect).
//   2. `await auth.WhenAuthenticated` blocks until login succeeds (or throws AuthException if the token is rejected).
//   3. Only AFTER auth does the protected Ping(110) request succeed — proving the gate was closed until then.
//
// Try it:
//   dotnet run --project examples/Auth/Auth.Client               → authenticates, Ping returns "pong"
//   dotnet run --project examples/Auth/Auth.Client -- wrongtoken → auth fails, no pong

using Auth.Client;
using Auth.Shared;
using SetNet.Auth;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Protocol;

SetNetSerializer.Use(new MessagePackNetSerializer());
AuthRuntime.Enable();   // load SetNet.Auth so its handshake handlers are discoverable

var token = args.Length > 0 ? args[0] : "letmein";

var client = new DemoClient(new Configuration { Host = "127.0.0.1", Port = 5311 });

// Attach auth before connecting. A fixed token here; a real app would pass a token provider that fetches a fresh
// token from its account backend: client.UseAuth(() => accountService.GetFreshTokenAsync()).
var auth = client.UseAuth(token);
auth.Authenticated += session => Console.WriteLine($"[client] authenticated as {session.AccountId} (session {session.SessionId})");
auth.AuthFailed += reason => Console.WriteLine($"[client] auth failed: {reason}");

Console.WriteLine($"Connecting to 127.0.0.1:5311 with token \"{token}\"...");
await client.ConnectAsync();

try
{
    // Blocks until the automatic login completes; throws AuthException if the server rejected the token.
    var session = await auth.WhenAuthenticated;
    Console.WriteLine($"[client] gate open — logged in as {session.AccountId}");

    // Now the protected app channel works. Before auth this request would be dropped by the gate.
    var pong = await client.RequestAsync<PingRequest, PongReply>(
        PingChannel.Id, (ushort)PingOp.Ping, new PingRequest { Note = "hello after auth" });
    Console.WriteLine($"[client] Ping succeeded → {pong.Reply} (echo: \"{pong.Echo}\")");
    Console.WriteLine("[client] The gate was closed until we authenticated — this pong proves it opened.");
}
catch (AuthException ex)
{
    Console.WriteLine($"[client] authentication rejected: {ex.Message}");
    Console.WriteLine("[client] The gate stayed shut, so the Ping was never attempted. Try token \"letmein\".");
}

client.Disconnect();
Console.WriteLine("Done.");
