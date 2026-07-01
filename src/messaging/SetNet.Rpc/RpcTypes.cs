namespace SetNet.Rpc
{
    /// <summary>
    /// Reserved wire message-type identifiers the RPC layer uses for its request/response envelopes. They sit
    /// just below SetNet's reserved system range (65533–65535) so they don't collide with normal application
    /// message types (which start low). Do not use these ids for your own messages.
    /// </summary>
    public static class RpcTypes
    {
        /// <summary>Type id for an RPC request envelope (caller → callee).</summary>
        public const ushort Request = ushort.MaxValue - 4;   // 65531

        /// <summary>Type id for an RPC response envelope (callee → caller).</summary>
        public const ushort Response = ushort.MaxValue - 3;  // 65532
    }
}
