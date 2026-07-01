namespace SetNet.Tests.Data;

public enum MessageTypes
{
    Empty = 0,
    PositionChanged = 123,
    LossPing = 200,
    UpdateClientId = 65535
}
