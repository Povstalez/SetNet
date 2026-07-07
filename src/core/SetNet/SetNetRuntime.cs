using System;
using System.Collections.Generic;
using SetNet.Core.Commands;
using SetNet.Messaging;
using SetNet.Protocol;

namespace SetNet
{
    /// <summary>
    /// Scoped SetNet runtime state. A runtime owns serializer selection, explicit handler registration, and
    /// long-lived module resources for one embedded networking environment. <see cref="Default"/> preserves the
    /// historical process-wide behaviour used by <see cref="SetNetSerializer"/>.
    /// </summary>
    public sealed class SetNetRuntime : IDisposable
    {
        private readonly List<IDisposable> _modules = new List<IDisposable>();
        private readonly object _modulesLock = new object();
        private ISerializer _serializer = new UnconfiguredSerializer();
        private bool _disposed;

        /// <summary>The backward-compatible process-wide runtime used when a configuration does not specify one.</summary>
        public static SetNetRuntime Default { get; } = new SetNetRuntime();

        /// <summary>Creates an isolated SetNet runtime.</summary>
        public SetNetRuntime()
        {
            ProtocolSubscriptions = new ProtocolSubscriptionRegistry(this);
        }

        /// <summary>Explicit and auto-discovered message-handler registration for this runtime.</summary>
        public HandlerRegistry Handlers { get; } = new HandlerRegistry();

        /// <summary>Client-side protocol push-event subscriptions scoped to this runtime.</summary>
        public ProtocolSubscriptionRegistry ProtocolSubscriptions { get; }

        /// <summary>The serializer used by this runtime for typed sends and handler dispatch.</summary>
        public ISerializer Serializer => _serializer;

        /// <summary>Registers the serializer used by this runtime. Call before connecting or starting a server.</summary>
        public SetNetRuntime UseSerializer(ISerializer serializer)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            return this;
        }

        /// <summary>Serializes a message with this runtime's serializer.</summary>
        public byte[] Serialize<T>(T value) => _serializer.Serialize(value);

        /// <summary>Deserializes a message with this runtime's serializer.</summary>
        public T Deserialize<T>(byte[] data) => _serializer.Deserialize<T>(data);

        /// <summary>
        /// Tracks a long-lived module resource so it can be disposed with the runtime. Server/client instances also
        /// expose module registration for resources whose lifetime should follow an endpoint instead.
        /// </summary>
        public T RegisterModule<T>(T module) where T : IDisposable
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            lock (_modulesLock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SetNetRuntime));
                _modules.Add(module);
            }
            return module;
        }

        /// <summary>Disposes modules registered with this runtime in reverse registration order.</summary>
        public void Dispose()
        {
            IDisposable[] modules;
            lock (_modulesLock)
            {
                if (_disposed) return;
                _disposed = true;
                modules = _modules.ToArray();
                _modules.Clear();
            }

            for (var i = modules.Length - 1; i >= 0; i--)
            {
                try { modules[i].Dispose(); } catch { }
            }
        }
    }
}
