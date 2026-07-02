namespace FileTransfer.Shared;

/// <summary>
/// Shared constants for the FileTransfer example. SetNet.Streams uses its own reserved wire types
/// (65492 / 65493) and a byte[] protocol, so the two ends only need to agree on connection defaults.
/// </summary>
public static class FileTransferProtocol
{
    /// <summary>Default host both ends bind to / connect to.</summary>
    public const string DefaultHost = "127.0.0.1";

    /// <summary>Default TCP port for the file-transfer server.</summary>
    public const int DefaultPort = 5320;
}
