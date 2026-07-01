using MessagePack;

namespace SetNet.Tests.Data;

[MessagePackObject]
public class UpdateClientIdMessage
{
    [Key(0)]
    public Guid ClientId { get; set; }
}