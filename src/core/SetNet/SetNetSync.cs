namespace SetNet
{
    /// <summary>
    /// Single-threaded-host support (Unity WebGL). SetNet's internal <c>await</c>s use
    /// <c>.ConfigureAwait(SetNetSync.ContinueOnCapturedContext)</c>. On a normal multi-threaded host this is left at
    /// its default of <see langword="false"/> — continuations resume on the thread pool, the correct, deadlock-free
    /// behaviour for a server/library, and identical to a bare <c>ConfigureAwait(false)</c>.
    /// <para>
    /// In a single-threaded environment such as <b>Unity WebGL</b> there is <i>no thread pool</i>, so those
    /// thread-pool continuations never run and every async operation (connect, receive, send, dispatch) hangs. Set
    /// this to <see langword="true"/> once at startup <b>and install a <see cref="System.Threading.SynchronizationContext"/>
    /// that is pumped on the main thread</b> (e.g. from Unity's <c>Update()</c>); continuations then resume on that
    /// context — i.e. the main thread — instead of the missing pool.
    /// </para>
    /// Flip it before creating any client/connection. It is a plain static (no threading concerns on the target
    /// single-threaded host).
    /// </summary>
    public static class SetNetSync
    {
        /// <summary>
        /// When <see langword="true"/>, awaited continuations resume on the captured <see cref="System.Threading.SynchronizationContext"/>
        /// (the main thread) rather than the thread pool. Default <see langword="false"/> (standard behaviour).
        /// </summary>
        public static bool ContinueOnCapturedContext = false;
    }
}
