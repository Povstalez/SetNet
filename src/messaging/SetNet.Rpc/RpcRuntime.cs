namespace SetNet.Rpc
{
    /// <summary>
    /// One-time RPC bootstrap. SetNet discovers handlers by scanning <b>loaded</b> assemblies once (cached on the
    /// first client/server construction). Call <see cref="Enable"/> at startup — before you construct your
    /// <c>BaseClient</c>/<c>BaseServer</c> — to guarantee this assembly is loaded so the RPC request/response
    /// handlers are found. It is a cheap no-op beyond forcing the load, and safe to call more than once.
    /// </summary>
    public static class RpcRuntime
    {
        /// <summary>
        /// Ensures the RPC layer is discoverable. Touching this method loads the <c>SetNet.Rpc</c> assembly, so
        /// the reserved-type handlers are present when SetNet builds its dispatch tables. Call it once at startup.
        /// </summary>
        public static void Enable()
        {
            // Referencing a type from this assembly here is enough to force the runtime to load it before the
            // SetNet command executors scan AppDomain assemblies.
            _ = RpcTypes.Request;
        }
    }
}
