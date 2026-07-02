using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Messaging;

namespace SetNet.Protocol
{
    /// <summary>
    /// Builds an <see cref="IChannelService"/> that routes each inbound op to a matching <see cref="OpAttribute"/>
    /// method on the channel instance — so a channel with many operations is written as many small methods instead
    /// of one big <c>switch</c>. Parameter binding and reply serialization are resolved once, at build time.
    /// </summary>
    internal static class OpRouter
    {
        /// <summary>Builds a router over an instance's <c>[Op]</c> methods, or null if it has none.</summary>
        public static IChannelService? Build(object instance)
        {
            var map = new Dictionary<ushort, OpInvoker>();
            foreach (var method in instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<OpAttribute>();
                if (attr == null) continue;
                if (map.ContainsKey(attr.Op))
                    throw new InvalidOperationException($"Duplicate [Op({attr.Op})] on {instance.GetType().FullName}.");
                map[attr.Op] = new OpInvoker(method);
            }
            return map.Count == 0 ? null : new OpRouterService(instance, map);
        }
    }

    /// <summary>The <see cref="IChannelService"/> produced by <see cref="OpRouter"/>: dispatches by op to the built invokers.</summary>
    internal sealed class OpRouterService : IChannelService
    {
        private readonly object _instance;
        private readonly Dictionary<ushort, OpInvoker> _ops;

        public OpRouterService(object instance, Dictionary<ushort, OpInvoker> ops)
        {
            _instance = instance;
            _ops = ops;
        }

        public Task HandleAsync(ChannelRequest request)
        {
            if (_ops.TryGetValue(request.Op, out var invoker))
                return invoker.InvokeAsync(_instance, request);
            // Unknown op: fail a request so the caller doesn't hang; ignore a fire-and-forget send.
            if (request.ExpectsReply)
                throw new ProtocolException($"No [Op({request.Op})] handler on channel {request.Channel}.");
            return Task.CompletedTask;
        }
    }

    /// <summary>How a handler method's return value maps to the protocol reply.</summary>
    internal enum ReplyKind { None, Raw, Typed }

    /// <summary>
    /// A resolved <c>[Op]</c> method: its parameter binders (peer / request / raw body / typed body) and its reply
    /// plan (none / raw / serialized), all computed once so per-call dispatch is just bind → invoke → reply.
    /// </summary>
    internal sealed class OpInvoker
    {
        private static readonly MethodInfo SerializeDef = typeof(SetNetSerializer).GetMethods()
            .First(m => m.Name == nameof(SetNetSerializer.Serialize) && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
        private static readonly MethodInfo DeserializeDef = typeof(SetNetSerializer).GetMethods()
            .First(m => m.Name == nameof(SetNetSerializer.Deserialize) && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);

        private readonly MethodInfo _method;
        private readonly Func<ChannelRequest, object?>[] _binders;
        private readonly bool _isAsync;
        private readonly ReplyKind _replyKind;
        private readonly Func<object?, byte[]>? _serialize;

        public OpInvoker(MethodInfo method)
        {
            _method = method;

            var parameters = method.GetParameters();
            _binders = new Func<ChannelRequest, object?>[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                _binders[i] = BuildBinder(parameters[i].ParameterType);

            // Resolve the reply plan from the (unwrapped) return type.
            var rt = method.ReturnType;
            Type unwrapped;
            if (rt == typeof(void)) { _isAsync = false; unwrapped = typeof(void); }
            else if (rt == typeof(Task)) { _isAsync = true; unwrapped = typeof(void); }
            else if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>)) { _isAsync = true; unwrapped = rt.GetGenericArguments()[0]; }
            else { _isAsync = false; unwrapped = rt; }

            if (unwrapped == typeof(void)) _replyKind = ReplyKind.None;
            else if (unwrapped == typeof(byte[])) _replyKind = ReplyKind.Raw;
            else
            {
                _replyKind = ReplyKind.Typed;
                var ser = SerializeDef.MakeGenericMethod(unwrapped);
                _serialize = result => (byte[])ser.Invoke(null, new[] { result })!;
            }
        }

        private static Func<ChannelRequest, object?> BuildBinder(Type parameterType)
        {
            if (parameterType == typeof(BasePeer)) return req => req.Peer;
            if (parameterType == typeof(ChannelRequest)) return req => req;
            if (parameterType == typeof(byte[])) return req => req.RawBody;
            // Anything else is the typed request body, deserialized via the app serializer.
            var des = DeserializeDef.MakeGenericMethod(parameterType);
            return req => des.Invoke(null, new object[] { req.RawBody });
        }

        public async Task InvokeAsync(object instance, ChannelRequest request)
        {
            var args = new object?[_binders.Length];
            for (var i = 0; i < _binders.Length; i++) args[i] = _binders[i](request);

            object? ret;
            try { ret = _method.Invoke(instance, args); }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                // Surface the real handler exception (ProtocolException etc.) instead of the reflection wrapper.
                throw tie.InnerException;
            }

            object? result = null;
            if (ret is Task task)
            {
                await task.ConfigureAwait(false);
                if (_replyKind != ReplyKind.None) result = task.GetType().GetProperty("Result")?.GetValue(task);
            }
            else if (!_isAsync)
            {
                result = ret;
            }

            switch (_replyKind)
            {
                case ReplyKind.Raw:
                    await request.ReplyRawAsync((byte[])(result ?? Array.Empty<byte>())).ConfigureAwait(false);
                    break;
                case ReplyKind.Typed:
                    await request.ReplyRawAsync(result == null ? Array.Empty<byte>() : _serialize!(result)).ConfigureAwait(false);
                    break;
                case ReplyKind.None:
                    break;
            }
        }
    }
}
