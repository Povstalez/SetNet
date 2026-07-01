using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Messaging;

namespace SetNet.Rpc
{
    /// <summary>Non-generic adapter that deserializes a request body, invokes a typed RPC handler, and serializes the response.</summary>
    internal interface IRpcInvoker
    {
        /// <summary>Runs the handler for a serialized request body and returns the serialized response body.</summary>
        Task<byte[]> InvokeAsync(BasePeer peer, byte[] requestBody);
    }

    /// <summary>
    /// Wraps a typed <see cref="IRpcHandler{TRequest,TResponse}"/>: deserializes the request body into
    /// <typeparamref name="TRequest"/> via the configured <see cref="SetNetSerializer"/>, invokes the handler,
    /// and serializes <typeparamref name="TResponse"/> back to bytes.
    /// </summary>
    internal sealed class RpcMethodInvoker<TRequest, TResponse> : IRpcInvoker
    {
        private readonly IRpcHandler<TRequest, TResponse> _handler;

        /// <summary>Wraps the given typed RPC handler.</summary>
        public RpcMethodInvoker(IRpcHandler<TRequest, TResponse> handler) => _handler = handler;

        /// <inheritdoc/>
        public async Task<byte[]> InvokeAsync(BasePeer peer, byte[] requestBody)
        {
            var request = SetNetSerializer.Deserialize<TRequest>(requestBody);
            var response = await _handler.HandleAsync(peer, request).ConfigureAwait(false);
            return SetNetSerializer.Serialize(response);
        }
    }

    /// <summary>
    /// Discovers <see cref="IRpcHandler{TRequest,TResponse}"/> implementations decorated with
    /// <see cref="RpcMethodAttribute"/> across loaded assemblies, maps each method id to an invoker, and routes
    /// incoming requests to them. Discovery runs once, lazily, and is cached.
    /// </summary>
    internal static class RpcMethodDispatcher
    {
        private static readonly Lazy<Dictionary<ushort, IRpcInvoker>> _invokers =
            new Lazy<Dictionary<ushort, IRpcInvoker>>(Discover, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Routes a request body to the handler registered for <paramref name="methodId"/> and returns its response body.</summary>
        /// <exception cref="RpcException">If no handler is registered for the method id.</exception>
        public static Task<byte[]> InvokeAsync(ushort methodId, BasePeer peer, byte[] requestBody)
        {
            if (!_invokers.Value.TryGetValue(methodId, out var invoker))
                throw new RpcException($"No RPC method registered for id {methodId}.");
            return invoker.InvokeAsync(peer, requestBody);
        }

        private static Dictionary<ushort, IRpcInvoker> Discover()
        {
            var map = new Dictionary<ushort, IRpcInvoker>();
            var openHandler = typeof(IRpcHandler<,>);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || type.IsInterface) continue;

                    var closed = type.GetInterfaces().FirstOrDefault(i =>
                        i.IsGenericType && i.GetGenericTypeDefinition() == openHandler);
                    if (closed == null) continue;

                    var attr = type.GetCustomAttribute<RpcMethodAttribute>();
                    if (attr == null) continue;

                    var args = closed.GetGenericArguments();          // [TRequest, TResponse]
                    var handler = Activator.CreateInstance(type);
                    var invoker = (IRpcInvoker)Activator.CreateInstance(
                        typeof(RpcMethodInvoker<,>).MakeGenericType(args[0], args[1]), handler);
                    map[attr.MethodId] = invoker;
                }
            }

            return map;
        }
    }
}
