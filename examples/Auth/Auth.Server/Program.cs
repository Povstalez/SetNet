// Auth server entry point.
//   dotnet run --project examples/Auth/Auth.Server -- [host] [port]
// Defaults: 127.0.0.1 5311.
//
// SetNet.Auth installs an ENFORCED GATE: until a peer authenticates, all of its application frames are dropped —
// only the auth handshake passes. The token "letmein" is accepted (see DemoAuthenticator); anything else is
// rejected. The protected endpoint is a Ping channel (id 110) that replies "pong" — reachable only after auth.
//
// NOTE: run over TLS in production so tokens aren't sent in the clear (this demo uses plain TCP for simplicity).

using Auth.Server;
using SetNet.Auth;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;

// The core library bundles no serializer; register the MessagePack adapter once at startup.
SetNetSerializer.Use(new MessagePackNetSerializer());
// Load SetNet.Auth BEFORE constructing the server so its auth handlers are discovered.
AuthRuntime.Enable();

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5311;

var server = new DemoServer(new Configuration { Host = host, Port = port });
_ = server.StartAsync();

Console.WriteLine($"Auth server on {host}:{port}. Token \"letmein\" opens the gate; Ping(110) is protected. Press Enter to stop.");
Console.ReadLine();
await server.StopAsync();
Console.WriteLine("Server stopped.");
