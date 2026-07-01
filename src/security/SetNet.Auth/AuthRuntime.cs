namespace SetNet.Auth
{
    /// <summary>
    /// One-time auth bootstrap. SetNet discovers handlers by scanning <b>loaded</b> assemblies once (cached on the
    /// first client/server construction). Call <see cref="Enable"/> at startup — before you construct your
    /// <c>BaseClient</c>/<c>BaseServer</c> — so this assembly is loaded and the auth request/response handlers are
    /// found. Cheap no-op beyond forcing the load; safe to call more than once.
    /// </summary>
    public static class AuthRuntime
    {
        /// <summary>Ensures the auth layer is discoverable. Call once at startup.</summary>
        public static void Enable()
        {
            _ = AuthTypes.Request;
        }
    }
}
