namespace SetNet.Rooms
{
    /// <summary>
    /// One-time rooms bootstrap. Call <see cref="Enable"/> at startup — before constructing your
    /// <c>BaseClient</c>/<c>BaseServer</c> — so this assembly is loaded and the room handlers are discovered.
    /// Cheap no-op beyond forcing the load; safe to call more than once.
    /// </summary>
    public static class RoomsRuntime
    {
        /// <summary>Ensures the rooms layer is discoverable. Call once at startup.</summary>
        public static void Enable()
        {
            _ = RoomTypes.Command;
        }
    }
}
