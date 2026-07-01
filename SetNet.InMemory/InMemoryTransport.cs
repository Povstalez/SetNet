using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;

namespace SetNet.InMemory
{
    /// <summary>
    /// An in-process loopback transport, plugged into SetNet via <see cref="Configuration.CustomTransport"/>. A client
    /// and a server that share the same <c>Host:Port</c> connect to each other entirely in memory — no sockets, no OS
    /// networking — while everything above the transport (handlers, RPC, rooms, auth) behaves exactly as it would over
    /// TCP. This makes it ideal for fast, deterministic integration tests and for co-hosting a client and server in one
    /// process. Delivery is reliable and ordered, so <see cref="DeliveryMethod"/> is ignored.
    /// </summary>
    /// <remarks>
    /// Call <see cref="InMemoryTransportExtensions.UseInMemory"/> on both the client's and the server's configuration.
    /// The server must be started (its listener registered) before the client connects, just like a real listener.
    /// </remarks>
    public sealed class InMemoryTransport : ITransportProvider
    {
        /// <inheritdoc/>
        public ITransportConnector CreateConnector(Configuration config) => new InMemoryConnector();

        /// <inheritdoc/>
        public ITransportListener CreateListener(Configuration config) => new InMemoryListener(InMemoryHub.Key(config));
    }

    /// <summary>Fluent helper for enabling the in-memory transport.</summary>
    public static class InMemoryTransportExtensions
    {
        /// <summary>Switches the configuration to the in-memory loopback transport (sets <see cref="TransportType.Custom"/> + an <see cref="InMemoryTransport"/>).</summary>
        /// <param name="config">The configuration to modify.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static Configuration UseInMemory(this Configuration config)
        {
            config.TransportType = TransportType.Custom;
            config.CustomTransport = new InMemoryTransport();
            return config;
        }
    }

    /// <summary>Process-wide registry mapping a <c>Host:Port</c> key to the listener bound there, so a connector can find it.</summary>
    internal static class InMemoryHub
    {
        private static readonly ConcurrentDictionary<string, InMemoryListener> Listeners
            = new ConcurrentDictionary<string, InMemoryListener>();

        /// <summary>The endpoint key an in-memory client and server rendezvous on.</summary>
        public static string Key(Configuration config) => (config.Host ?? "") + ":" + config.Port;

        public static void Register(string key, InMemoryListener listener)
        {
            if (!Listeners.TryAdd(key, listener))
                throw new InvalidOperationException($"An in-memory listener is already bound to '{key}'.");
        }

        public static void Unregister(string key, InMemoryListener listener)
        {
            // Remove only if we're still the bound listener (guards against clobbering a rebind on the same key).
            if (Listeners.TryGetValue(key, out var current) && ReferenceEquals(current, listener))
                Listeners.TryRemove(key, out _);
        }

        public static InMemoryListener? Find(string key)
            => Listeners.TryGetValue(key, out var listener) ? listener : null;
    }

    /// <summary>Client-side in-memory dialer: finds the listener bound to the config's endpoint and links a pair to it.</summary>
    internal sealed class InMemoryConnector : ITransportConnector
    {
        /// <inheritdoc/>
        public Task<ITransportConnection> ConnectAsync(Configuration config, CancellationToken ct = default)
        {
            var key = InMemoryHub.Key(config);
            var listener = InMemoryHub.Find(key)
                ?? throw new IOException($"No in-memory listener is bound to '{key}'. Start the server (UseInMemory) before connecting.");

            var (client, server) = InMemoryConnection.CreatePair();
            if (!listener.Deliver(new AcceptedConnection(server, Guid.Empty, null)))
                throw new IOException($"The in-memory listener bound to '{key}' has stopped.");

            return Task.FromResult<ITransportConnection>(client);
        }
    }

    /// <summary>Server-side in-memory acceptor: surfaces the server ends of pairs created by connectors on the same endpoint.</summary>
    internal sealed class InMemoryListener : ITransportListener
    {
        private readonly string _key;
        private readonly AsyncChannel<AcceptedConnection> _accepts = new AsyncChannel<AcceptedConnection>();
        private int _started;

        public InMemoryListener(string key) => _key = key;

        /// <inheritdoc/>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            InMemoryHub.Register(_key, this);
        }

        /// <inheritdoc/>
        public void Stop()
        {
            if (Interlocked.Exchange(ref _started, 0) != 1) return;
            InMemoryHub.Unregister(_key, this);
            _accepts.Complete();   // unblock AcceptAsync so the accept loop ends
        }

        /// <summary>Called by a connector to hand the server the server-end of a freshly linked pair. Returns false if stopped.</summary>
        public bool Deliver(AcceptedConnection accepted)
        {
            if (Volatile.Read(ref _started) != 1) return false;
            _accepts.Write(accepted);
            return true;
        }

        /// <inheritdoc/>
        public async Task<AcceptedConnection?> AcceptAsync(CancellationToken ct = default)
        {
            var (ok, accepted) = await _accepts.ReadAsync(ct).ConfigureAwait(false);
            return ok ? accepted : null;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Stop();
            _accepts.Dispose();
        }
    }
}
