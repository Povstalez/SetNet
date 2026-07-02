using MessagePack;

namespace Presence.Shared;

/// <summary>A tiny topic-based pub/sub built entirely on the unified <c>SetNet.Protocol</c> — no companion module.</summary>
public static class PresenceChannel { public const ushort Id = 300; }

/// <summary>Client → server ops (all fire-and-forget).</summary>
public enum PresenceOp : ushort { Subscribe = 1, Unsubscribe = 2, Publish = 3 }

/// <summary>Server → client events.</summary>
public enum PresenceEvt : ushort { Message = 10 }

/// <summary>Subscribe/unsubscribe to a topic.</summary>
[MessagePackObject]
public class TopicRef { [Key(0)] public string Topic { get; set; } = ""; }

/// <summary>Publish text to a topic.</summary>
[MessagePackObject]
public class PublishReq { [Key(0)] public string Topic { get; set; } = ""; [Key(1)] public string Text { get; set; } = ""; }

/// <summary>A message delivered to a topic's subscribers.</summary>
[MessagePackObject]
public class TopicMessage
{
    [Key(0)] public string Topic { get; set; } = "";
    [Key(1)] public string From { get; set; } = "";
    [Key(2)] public string Text { get; set; } = "";
}
