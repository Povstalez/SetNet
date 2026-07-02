using MessagePack;

namespace Economy.Shared;

/// <summary>The world/actions app channel (above the shipped modules' 1–25 range).</summary>
public static class WorldChannel { public const ushort Id = 200; }

/// <summary>Client → server world ops.</summary>
public enum WorldOp : ushort { Drop = 1 }

/// <summary>Server → client world events.</summary>
public enum WorldEvt : ushort { ItemDropped = 10 }

/// <summary>Client → server (fire-and-forget): drop an item from my inventory into the world.</summary>
[MessagePackObject]
public class DropReq
{
    [Key(0)] public string ItemId { get; set; } = "";
    [Key(1)] public long Count { get; set; }
}

/// <summary>Server → client event: someone in my room dropped an item.</summary>
[MessagePackObject]
public class ItemDropped
{
    [Key(0)] public string PlayerId { get; set; } = "";
    [Key(1)] public string ItemId { get; set; } = "";
    [Key(2)] public long Count { get; set; }
}
