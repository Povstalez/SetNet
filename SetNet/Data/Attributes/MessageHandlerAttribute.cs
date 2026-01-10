using System;

namespace SetNet.Data.Attributes
{
    [AttributeUsage(AttributeTargets.Class)]
    public class MessageHandlerAttribute : Attribute
    {
        public ushort MessageType { get; }

        public MessageHandlerAttribute(ushort messageType)
        {
            MessageType = messageType;
        }
    }
}