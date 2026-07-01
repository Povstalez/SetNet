namespace SetNet.Matchmaking
{
    /// <summary>
    /// One-time matchmaking bootstrap. Call <see cref="Enable"/> at startup — before constructing your
    /// <c>BaseClient</c>/<c>BaseServer</c> — so this assembly is loaded and the matchmaking handlers are discovered.
    /// Cheap no-op beyond forcing the load; safe to call more than once. (Matchmaking rides on top of rooms, so also
    /// call <c>RoomsRuntime.Enable()</c> and enable rooms on your server/client.)
    /// </summary>
    public static class MatchmakingRuntime
    {
        /// <summary>Ensures the matchmaking layer is discoverable. Call once at startup.</summary>
        public static void Enable()
        {
            _ = MatchTypes.Command;
        }
    }
}
