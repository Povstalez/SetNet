using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SetNet.Messaging
{
    public class MessageProcessor
    {
        private readonly Dictionary<ushort, Func<byte[], Task>> _handlers = new Dictionary<ushort, Func<byte[], Task>>();
        private readonly Dictionary<ushort, Action<byte[]>> _handlersActions = new Dictionary<ushort, Action<byte[]>>();

        public void RegisterHandler(ushort type, Func<byte[], Task> handler)
        {
            _handlers[type] = handler;
        }
        
        public void RegisterHandler(ushort type, Action<byte[]> handler)
        {
            _handlersActions[type] = handler;
        }

        public void ProcessMessage(ushort type, byte[] data)
        {
            if (_handlers.TryGetValue(type, out var handler))
            {
                handler(data);
            }
            else if (_handlersActions.TryGetValue(type, out var handlerAction))
            {
                handlerAction(data);
            }
        }
    }
}