using MessagePack;

namespace SetNet.Tests.Data;

[MessagePackObject]
public class TestMessage
{
    [Key(0)]
    public float X { get; set; }
    [Key(1)]
    public float Y { get; set; }
}