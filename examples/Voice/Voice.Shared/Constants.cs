namespace Voice.Shared;

/// <summary>
/// Shared constants for the Voice example. SetNet.Voice uses its own reserved wire types
/// (65503 / 65504 / 65505) and a byte[] protocol, so the two ends only need to agree on
/// connection defaults and the channel to talk on.
/// </summary>
public static class VoiceProtocol
{
    /// <summary>Default host both ends bind to / connect to.</summary>
    public const string DefaultHost = "127.0.0.1";

    /// <summary>Default TCP port for the voice relay server.</summary>
    public const int DefaultPort = 5321;

    /// <summary>The numeric voice channel every client in this demo joins.</summary>
    public const ushort Channel = 1;
}
