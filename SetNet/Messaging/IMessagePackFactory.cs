namespace SetNet.Messaging
{
    /// <summary>
    /// Reserved extension point for customizing how MessagePack serialization is configured (for example,
    /// supplying custom formatters, resolvers, or serializer options). Currently a placeholder with no
    /// members; it exists to mark the intended seam in the serialization layer for future pluggable factory
    /// implementations.
    /// </summary>
    public class IMessagePackFactory
    {

    }
}
