using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private static readonly HashSet<ProtocolSubscriptionRegistry> Done = new HashSet<ProtocolSubscriptionRegistry>();
        private static readonly object Gate = new object();

        private static readonly MethodInfo DeserializeDef = typeof(ISerializer).GetMethods()
            .First(m => m.Name == nameof(ISerializer.Deserialize) && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);

        /// <summary>Scans and auto-subscribes once; a no-op after the first call.</summary>
        public static void EnsureDiscovered() => EnsureDiscovered(SetNetRuntime.Default.ProtocolSubscriptions);

        /// <summary>Scans and auto-subscribes into a runtime-scoped subscription registry once.</summary>
        public static void EnsureDiscovered(ProtocolSubscriptionRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            lock (Gate)
            {
                if (!Done.Add(registry)) return;
                Scan(registry);
            }
        }

        private static void Scan(ProtocolSubscriptionRegistry registry)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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
