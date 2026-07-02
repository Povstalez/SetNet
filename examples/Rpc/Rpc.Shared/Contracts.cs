using MessagePack;

namespace Rpc.Shared;

/// <summary>RPC method ids shared by both ends (each matches a server-side <c>[RpcMethod]</c>).</summary>
public static class RpcMethods
{
    /// <summary>Return the server's current UTC time.</summary>
    public const ushort GetTime = 1;
    /// <summary>Add two integers.</summary>
    public const ushort Add = 2;
}

/// <summary>Request for <see cref="RpcMethods.GetTime"/> (no parameters).</summary>
[MessagePackObject] public class TimeRequest { }

/// <summary>Reply for <see cref="RpcMethods.GetTime"/>.</summary>
[MessagePackObject] public class TimeReply { [Key(0)] public string UtcNow { get; set; } = ""; }

/// <summary>Request for <see cref="RpcMethods.Add"/>.</summary>
[MessagePackObject] public class AddRequest { [Key(0)] public int A { get; set; } [Key(1)] public int B { get; set; } }

/// <summary>Reply for <see cref="RpcMethods.Add"/>.</summary>
[MessagePackObject] public class AddReply { [Key(0)] public int Sum { get; set; } }
