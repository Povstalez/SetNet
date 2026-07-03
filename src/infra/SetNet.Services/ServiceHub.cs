using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using SetNet.Core;

namespace SetNet.Services
{
    /// <summary>
    /// A tiny type-keyed service locator so you don't have to hand-store every <c>UseXxx()</c> instance in fields and
    /// thread them everywhere. Register each once — <c>hub.Add(server.UseInventory())</c> — and resolve it anywhere by
    /// type. Use it three ways, mix freely:
    /// <list type="bullet">
    ///   <item><b>Ambient</b> — <c>new ServiceHub().MakeCurrent()</c>, then <c>Service.Get&lt;InventoryServer&gt;()</c> from anywhere.</item>
    ///   <item><b>Per-server / per-client</b> — <c>server.Services().Add(...)</c> and <c>server.Services().Get&lt;T&gt;()</c> (isolated per owner).</item>
    ///   <item><b>Explicit</b> — hold the <see cref="ServiceHub"/> yourself and pass it where you like.</item>
    /// </list>
    /// It stores concrete instances by their type; it does not construct anything (that stays with the modules' fluent
    /// <c>UseXxx()</c> factories). Thread-safe.
    /// </summary>
    public sealed class ServiceHub
    {
        private readonly ConcurrentDictionary<Type, object> _map = new ConcurrentDictionary<Type, object>();

        /// <summary>The ambient hub set by <see cref="MakeCurrent"/>, or null. Read it through <see cref="Service"/>.</summary>
        public static ServiceHub? Current { get; set; }

        /// <summary>Makes this the ambient <see cref="Current"/> hub and returns it for chaining.</summary>
        public ServiceHub MakeCurrent()
        {
            Current = this;
            return this;
        }

        /// <summary>
        /// Registers <paramref name="instance"/> under its static type <typeparamref name="T"/> and returns it, so you can
        /// capture and register in one line: <c>var inv = hub.Add(server.UseInventory());</c>. A later <c>Add</c> of the
        /// same type replaces it.
        /// </summary>
        public T Add<T>(T instance) where T : class
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            _map[typeof(T)] = instance;
            return instance;
        }

        /// <summary>Registers several instances by their runtime type at once (returns the hub for chaining).</summary>
        public ServiceHub AddAll(params object[] instances)
        {
            foreach (var i in instances)
                if (i != null) _map[i.GetType()] = i;
            return this;
        }

        /// <summary>Resolves the instance registered as <typeparamref name="T"/>; throws if none is registered.</summary>
        public T Get<T>() where T : class
            => TryGet<T>(out var value)
                ? value
                : throw new InvalidOperationException(
                    $"No service of type '{typeof(T).Name}' is registered. Did you call hub.Add(server.Use{Guess<T>()}())?");

        /// <summary>Resolves the instance registered as <typeparamref name="T"/>, or null if none.</summary>
        public T? GetOrNull<T>() where T : class => TryGet<T>(out var v) ? v : null;

        /// <summary>Tries to resolve the instance registered as <typeparamref name="T"/>.</summary>
        public bool TryGet<T>(out T value) where T : class
        {
            if (_map.TryGetValue(typeof(T), out var o) && o is T t) { value = t; return true; }
            value = null!;
            return false;
        }

        /// <summary>True if a service of type <typeparamref name="T"/> is registered.</summary>
        public bool Has<T>() where T : class => _map.ContainsKey(typeof(T));

        /// <summary>Removes the service of type <typeparamref name="T"/> if present.</summary>
        public bool Remove<T>() where T : class => _map.TryRemove(typeof(T), out _);

        /// <summary>Every registered instance (snapshot).</summary>
        public IReadOnlyCollection<object> All => _map.Values.ToArray();

        // Best-effort hint for the "did you forget to Add?" message (strips the trailing "Server"/"System").
        private static string Guess<T>()
        {
            var n = typeof(T).Name;
            if (n.EndsWith("Server", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 6);
            else if (n.EndsWith("System", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 6);
            return n;
        }
    }

    /// <summary>A static shortcut over the ambient <see cref="ServiceHub.Current"/>.</summary>
    public static class Service
    {
        private static ServiceHub Hub => ServiceHub.Current
            ?? throw new InvalidOperationException("No ambient ServiceHub. Call `new ServiceHub().MakeCurrent()` at startup, or use `server.Services()`.");

        /// <summary>Resolves <typeparamref name="T"/> from the ambient hub; throws if there's no hub or no such service.</summary>
        public static T Get<T>() where T : class => Hub.Get<T>();

        /// <summary>Resolves <typeparamref name="T"/> from the ambient hub, or null if there's no hub or no such service.</summary>
        public static T? GetOrNull<T>() where T : class => ServiceHub.Current?.GetOrNull<T>();

        /// <summary>Tries to resolve <typeparamref name="T"/> from the ambient hub.</summary>
        public static bool TryGet<T>(out T value) where T : class
        {
            var hub = ServiceHub.Current;
            if (hub != null) return hub.TryGet(out value);
            value = null!;
            return false;
        }
    }

    /// <summary>Per-owner service hubs bound to a <see cref="BaseServer"/> / <see cref="BaseClient"/>.</summary>
    public static class ServiceHubExtensions
    {
        private static readonly ConditionalWeakTable<BaseServer, ServiceHub> ServerHubs = new ConditionalWeakTable<BaseServer, ServiceHub>();
        private static readonly ConditionalWeakTable<BaseClient, ServiceHub> ClientHubs = new ConditionalWeakTable<BaseClient, ServiceHub>();

        /// <summary>The service hub for this server (created on first use; lives as long as the server).</summary>
        public static ServiceHub Services(this BaseServer server) => ServerHubs.GetValue(server, _ => new ServiceHub());

        /// <summary>The service hub for this client (created on first use; lives as long as the client).</summary>
        public static ServiceHub Services(this BaseClient client) => ClientHubs.GetValue(client, _ => new ServiceHub());
    }
}
