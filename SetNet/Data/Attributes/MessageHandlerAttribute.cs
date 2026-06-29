using System;

namespace SetNet.Data.Attributes
{
    /// <summary>
    /// Marks a message-handler class for reflection-based discovery and associates it with the
    /// wire message-type identifier it processes. <c>CommandExecutor</c> scans loaded assemblies
    /// for classes carrying this attribute (and implementing <see cref="SetNet.Data.IServerMessageHandler"/>
    /// or <see cref="SetNet.Data.IClientMessageHandler"/>) and registers them automatically, so handlers
    /// never have to be wired up by hand. It sits at the boundary between the routing layer and
    /// user-defined message logic.
    /// </summary>
    /// <remarks>
    /// Applies only to classes (<see cref="AttributeTargets.Class"/>). Each handler class is expected to
    /// declare a single message type; the <see cref="MessageType"/> value must match the identifier used by
    /// the sender for the handler to ever be invoked.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class)]
    public class MessageHandlerAttribute : Attribute
    {
        /// <summary>
        /// The wire message-type identifier this handler is responsible for. Used as the routing key when
        /// <c>CommandExecutor</c> registers the decorated handler, so incoming packets tagged with this value
        /// are dispatched to it.
        /// </summary>
        public ushort MessageType { get; }

        /// <summary>
        /// Creates the attribute and binds the decorated handler class to a specific message type.
        /// </summary>
        /// <param name="messageType">
        /// The wire message-type identifier (typically a cast from a user-defined <c>MessageTypes</c> enum)
        /// that selects which incoming messages this handler receives.
        /// </param>
        public MessageHandlerAttribute(ushort messageType)
        {
            MessageType = messageType;
        }
    }
}