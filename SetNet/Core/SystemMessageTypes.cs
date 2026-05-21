namespace SetNet.Core
{
    internal static class SystemMessageTypes
    {
        internal const ushort Ping = ushort.MaxValue - 1; // 65534
        internal const ushort Pong = ushort.MaxValue;     // 65535
    }
}
