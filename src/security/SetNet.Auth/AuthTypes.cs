namespace SetNet.Auth
{
    /// <summary>
    /// Reserved wire message-type ids for the auth handshake. They sit just below SetNet's core system range
    /// (65532–65535: Kick, UdpBindToken, Ping, Pong). Don't use these ids for your own messages. The <b>request</b>
    /// type is the one frame the enforced gate lets through before a peer authenticates.
    /// </summary>
    public static class AuthTypes
    {
        /// <summary>Type id for a login/resume request (client → server).</summary>
        public const ushort Request = ushort.MaxValue - 6;   // 65529

        /// <summary>Type id for an auth response (server → client).</summary>
        public const ushort Response = ushort.MaxValue - 5;  // 65530
    }

    /// <summary>Whether an auth request is a fresh login (with a login token) or a resume (with a reconnect token).</summary>
    internal enum AuthKind : byte
    {
        Login = 0,
        Resume = 1
    }
}
