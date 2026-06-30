using System.Collections.Concurrent;
using System.Threading.Tasks;
using MessagePack;
using SetNet.Config;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>Simple echo message used by the transport integration tests.</summary>
[MessagePackObject]
public class EchoMessage
{
    /// <summary>Arbitrary text payload echoed between client and server.</summary>
    [Key(0)]
    public string Text { get; set; } = "";
}

/// <summary>
/// Process-wide capture of what the test server and client received, so integration tests can assert
/// delivery. Reset at the start of each test; the integration tests run serially (see the collection).
/// </summary>
public static class TestInbox
{
    /// <summary>Texts the server's echo handler observed.</summary>
    public static readonly ConcurrentQueue<string> ServerReceived = new();

    /// <summary>Texts the client's echo handler observed (i.e. echoed back by the server).</summary>
    public static readonly ConcurrentQueue<string> ClientReceived = new();

    /// <summary>Clears both capture queues before a test runs.</summary>
    public static void Reset()
    {
        while (ServerReceived.TryDequeue(out _)) { }
        while (ClientReceived.TryDequeue(out _)) { }
    }
}

/// <summary>Test server peer exposing a public send so the echo handler can reply.</summary>
public class TestPeer : BasePeer
{
    /// <summary>Creates the peer from the accepted connection info.</summary>
    /// <param name="info">Per-connection metadata from the base server.</param>
    public TestPeer(PeerInfo info) : base(info) { }

    /// <summary>Public wrapper over the protected send so handlers can reply to this client.</summary>
    /// <typeparam name="T">The MessagePack-serializable message type.</typeparam>
    /// <param name="type">The wire message-type id.</param>
    /// <param name="message">The message to send.</param>
    /// <returns>A task that completes when the message is handed to the transport.</returns>
    public Task SendMessageAsync<T>(ushort type, T message) => SendAsync(type, message);

    /// <inheritdoc/>
    protected override void OnDisconnected() { }

    /// <inheritdoc/>
    protected override void OnError(string error) { }
}

/// <summary>Test server that creates a <see cref="TestPeer"/> per client and starts its receive loop.</summary>
public class TestServer : BaseServer
{
    /// <summary>Creates the server with the given configuration.</summary>
    /// <param name="config">Transport/endpoint settings.</param>
    public TestServer(Configuration config) : base(config) { }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo peerInfo)
    {
        var peer = new TestPeer(peerInfo);
        peer.StartReceive();
        return peer;
    }
}

/// <summary>Test server whose <see cref="OnNewClient"/> deliberately does NOT call StartReceive, to verify the framework starts it.</summary>
public class ForgetfulServer : BaseServer
{
    /// <summary>Creates the server with the given configuration.</summary>
    /// <param name="config">Transport/endpoint settings.</param>
    public ForgetfulServer(Configuration config) : base(config) { }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo peerInfo) => new TestPeer(peerInfo); // intentionally no StartReceive()
}

/// <summary>Test client that counts how many times <see cref="OnDisconnected"/> fires.</summary>
public class CountingClient : BaseClient
{
    /// <summary>Number of times the terminal disconnect callback has fired.</summary>
    public int DisconnectedCount;

    /// <summary>Creates the client with the given configuration.</summary>
    /// <param name="config">Transport/endpoint settings.</param>
    public CountingClient(Configuration config) : base(config) { }

    /// <inheritdoc/>
    protected override void OnConnected() { }

    /// <inheritdoc/>
    protected override void OnDisconnected() => System.Threading.Interlocked.Increment(ref DisconnectedCount);

    /// <inheritdoc/>
    protected override void OnError(string error) { }
}

/// <summary>Test client exposing a public echo-send helper.</summary>
public class TestClient : BaseClient
{
    /// <summary>Creates the client with the given configuration.</summary>
    /// <param name="config">Transport/endpoint settings.</param>
    public TestClient(Configuration config) : base(config) { }

    /// <summary>Sends an <see cref="EchoMessage"/> to the server with the chosen delivery method.</summary>
    /// <param name="text">The text to echo.</param>
    /// <param name="delivery">Reliable or unreliable delivery.</param>
    /// <returns>A task that completes when the message is handed to the transport.</returns>
    public Task SendEchoAsync(string text, DeliveryMethod delivery)
        => SendAsync((ushort)900, new EchoMessage { Text = text }, delivery);

    public Task SendEchoAsync(string text, DeliveryMethod delivery, byte channel)
        => SendAsync((ushort)900, new EchoMessage { Text = text }, delivery, channel);

    /// <summary>Exposes the protected batch flush for tests.</summary>
    public Task FlushManuallyAsync() => FlushAsync();

    /// <inheritdoc/>
    protected override void OnConnected() { }

    /// <inheritdoc/>
    protected override void OnDisconnected() { }

    /// <inheritdoc/>
    protected override void OnError(string error) { }
}

/// <summary>Server-side echo handler: records the text and echoes it back to the sender (type 901).</summary>
[MessageHandler(900)]
public class EchoServerHandler : IServerMessageHandler
{
    /// <inheritdoc/>
    public async Task HandleAsync(BasePeer peer, byte[] data)
    {
        var message = SetNet.Messaging.SetNetSerializer.Deserialize<EchoMessage>(data);
        TestInbox.ServerReceived.Enqueue(message.Text);
        await ((TestPeer)peer).SendMessageAsync((ushort)901, new EchoMessage { Text = message.Text });
    }
}

/// <summary>Client-side echo handler: records the text echoed back by the server (type 901).</summary>
[MessageHandler(901)]
public class EchoClientHandler : IClientMessageHandler
{
    /// <inheritdoc/>
    public Task HandleAsync(byte[] data)
    {
        var message = SetNet.Messaging.SetNetSerializer.Deserialize<EchoMessage>(data);
        TestInbox.ClientReceived.Enqueue(message.Text);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Marks the transport integration tests as a non-parallel collection. They bind real sockets and share
/// the static <see cref="TestInbox"/>, so they must not run concurrently with each other.
/// </summary>
[CollectionDefinition("integration", DisableParallelization = true)]
public class IntegrationCollection { }
