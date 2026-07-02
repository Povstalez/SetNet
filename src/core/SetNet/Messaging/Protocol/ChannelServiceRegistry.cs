using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SetNet.Core.Commands;

namespace SetNet.Protocol
{
    /// <summary>
    /// Discovers <see cref="IChannelService"/> implementations decorated with <see cref="ProtocolChannelAttribute"/>
    /// across loaded assemblies and maps each channel id to its (single, cached) service instance. Discovery is
    /// lazy: the first lookup scans, and a miss triggers one rescan so a module whose assembly loaded after the
    /// first scan (e.g. a late <c>UseXxx</c>) is still found. Instances are built through
    /// <see cref="HandlerActivator"/>, so DI-constructed services work too.
    /// </summary>
    internal static class ChannelServiceRegistry
    {
        private static readonly ConcurrentDictionary<ushort, IChannelService> Services
            = new ConcurrentDictionary<ushort, IChannelService>();
        private static readonly HashSet<Assembly> Scanned = new HashSet<Assembly>();
        private static readonly object Gate = new object();

        /// <summary>Returns the service for a channel, scanning (and rescanning once on miss) if necessary; null if none is registered.</summary>
        public static IChannelService? Get(ushort channel)
        {
            if (Services.TryGetValue(channel, out var svc)) return svc;
            Scan();
            return Services.TryGetValue(channel, out svc) ? svc : null;
        }

        /// <summary>Scans any not-yet-scanned loaded assemblies for channel services and registers them.</summary>
        private static void Scan()
        {
            lock (Gate)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!Scanned.Add(assembly)) continue;   // already scanned this assembly

                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

                    foreach (var type in types)
                    {
                        if (type == null || type.IsAbstract || type.IsInterface) continue;

                        var attr = type.GetCustomAttribute<ProtocolChannelAttribute>();
                        if (attr == null) continue;

                        // A [ProtocolChannel] class is a *server* channel only if it implements IChannelService
                        // (manual dispatch) or has [Op]-attributed methods (auto-routed). Skip client-only classes
                        // (e.g. those with just [Event] handlers) without instantiating them here.
                        var isService = typeof(IChannelService).IsAssignableFrom(type);
                        var hasOps = isService || type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Any(m => m.GetCustomAttribute<OpAttribute>() != null);
                        if (!hasOps) continue;

                        var instance = HandlerActivator.Create(type);
                        var service = instance as IChannelService ?? OpRouter.Build(instance);
                        if (service == null) continue;   // defensive: nothing to route

                        Services[attr.Channel] = service;   // last registration wins (module owns its channel)
                    }
                }
            }
        }
    }
}
