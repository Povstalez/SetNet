using MessagePack;

namespace SetNet.Tests.Data;

[MessagePackObject]
public class LossCountMessage
{
    [Key(0)]
    public int Seq { get; set; }

    [Key(1)]
    public bool ViaReliable { get; set; }
}
