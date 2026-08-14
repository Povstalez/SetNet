using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core.Commands;
using SetNet.Messaging;

namespace SetNet.Protocol
{
    /// <summary>
    /// One-time discovery of client-side <see cref="EventAttribute"/> handlers: scans loaded assemblies for
    /// <see cref="ProtocolChannelAttribute"/> classes with <c>[Event]</c> methods, instantiates each, and subscribes
    /// its methods into <see cref="ProtocolSubscriptions"/> — the declarative counterpart to imperative
    /// <c>client.On&lt;T&gt;</c>. Runs lazily the first time an event is dispatched (by which point the app and any
    /// enabled module assemblies are loaded), so it needs no client-init hook.
    /// </summary>
    internal static class ClientEventDiscovery
    {
        // Per-registry scan state, weakly referenced so a disposed scoped runtime's registry can be collected
        // (a strong static set would pin every runtime that ever received an event for the process lifetime).
        private static readonly ConditionalWeakTable<ProtocolSubscriptionRegistry, ScanState> States
            = new ConditionalWeakTable<ProtocolSubscriptionRegistry, ScanState>();

        private sealed class ScanState
        {
            public readonly HashSet<Assembly> Scanned = new HashSet<Assembly>();

            /// <summary>
            /// Raised high when the loaded-assembly set may have changed: before the first scan, and whenever the
            /// CLR reports a newly loaded assembly. Lowered once a scan has caught up.
            /// </summary>
            /// <remarks>
            /// This flag replaces polling <c>AppDomain.CurrentDomain.GetAssemblies().Length</c> on every dispatch.
            /// That call allocates a fresh array holding every loaded assembly (hundreds of bytes) each time it is
            /// made — invisible in request/response traffic, but a real garbage source in a game client that takes
            /// hundreds of push events per frame, where it was the largest single per-event allocation in the whole
            /// dispatch path. The CLR already tells us when the set actually changes, so the steady state is now
            /// one volatile bool read.
            /// </remarks>
            public volatile bool Dirty = true;

            public ScanState()
            {
                // The handler holds this state alive, and the state is reachable only through the weak table entry,
                // so a collected registry still drops its state — just one frame later than before.
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            }

            private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args) => Dirty = true;
        }

        private static readonly MethodInfo DeserializeDef = typeof(ISerializer).GetMethods()
            .First(m => m.Name == nameof(ISerializer.Deserialize) && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);

        /// <summary>Scans and auto-subscribes into the default runtime's registry.</summary>
        public static void EnsureDiscovered() => EnsureDiscovered(SetNetRuntime.Default.ProtocolSubscriptions);

        /// <summary>
        /// Subscribes declarative <c>[Event]</c> handlers into <paramref name="registry"/>, scanning any assemblies
        /// that have loaded since the last call — so a module enabled after the first event still gets its client
        /// event handlers wired (mirrors <see cref="ChannelServiceRegistry"/>'s rescan-on-miss instead of scanning
        /// exactly once, ever). Free on the steady state: with no assembly loaded since the last scan it returns on
        /// a single flag read, allocating nothing — this runs on every dispatched event.
        /// </summary>
        public static void EnsureDiscovered(ProtocolSubscriptionRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            var state = States.GetValue(registry, _ => new ScanState());

            // Fast path first, outside the lock: nothing loaded since the last scan, nothing to do.
            if (!state.Dirty) return;

            lock (state)
            {
                if (!state.Dirty) return;                                   // another thread scanned while we waited
                state.Dirty = false;                                        // clear before scanning: a load during the
                                                                            // scan re-raises it and we run again next time
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (state.Scanned.Add(assembly))                        // scan each assembly's [Event] handlers exactly once
                        ScanAssembly(registry, assembly);
            }
        }

        private static void ScanAssembly(ProtocolSubscriptionRegistry registry, Assembly assembly)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            foreach (var type in types)
            {
                if (type == null || type.IsAbstract || type.IsInterface) continue;

                var channel = type.GetCustomAttribute<ProtocolChannelAttribute>();
                if (channel == null) continue;

                var eventMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.GetCustomAttribute<EventAttribute>() != null)
                    .ToArray();
                if (eventMethods.Length == 0) continue;

                var instance = HandlerActivator.Create(type);
                foreach (var method in eventMethods)
                {
                    var ev = method.GetCustomAttribute<EventAttribute>()!;
                    // The returned IDisposable is intentionally not kept: attribute handlers live for the process.
                    registry.Add(channel.Channel, ev.Op, BuildCallback(registry, instance, method));
                }
            }
        }

        private static Action<byte[]> BuildCallback(ProtocolSubscriptionRegistry registry, object instance, MethodInfo method)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 0)
                return body => Invoke(method, instance, Array.Empty<object?>());

            if (parameters.Length == 1)
            {
                var pt = parameters[0].ParameterType;
                if (pt == typeof(byte[]))
                    return body => Invoke(method, instance, new object?[] { body });

                var des = DeserializeDef.MakeGenericMethod(pt);
                return body => Invoke(method, instance, new object?[] { des.Invoke(registry.Runtime.Serializer, new object[] { body }) });
            }

            throw new InvalidOperationException(
                $"[Event] method {method.DeclaringType?.Name}.{method.Name} must take zero parameters, one byte[], or one typed body.");
        }

        private static void Invoke(MethodInfo method, object instance, object?[] args)
        {
            object? ret;
            try { ret = method.Invoke(instance, args); }
            catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
            if (ret is Task task) _ = Await(task);   // async handler → fire-and-forget
        }

        private static async Task Await(Task task)
        {
            try { await task.ConfigureAwait(global::SetNet.SetNetSync.ContinueOnCapturedContext); } catch { /* isolate a faulty async event handler */ }
        }
    }
}
