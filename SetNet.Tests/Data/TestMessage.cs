using MessagePack;

namespace SetNet.Tests.Data;

[MessagePackObject]
public class TestMessage
{
    [Key(0)]
    public string Message { get; set; }
}