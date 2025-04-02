namespace SetNet.Messaging
{
    public static class MessagePackSerializer
    {
        public static byte[] Serialize<T>(T message) => MessagePack.MessagePackSerializer.Serialize(message);

        public static T Deserialize<T>(byte[] data) => MessagePack.MessagePackSerializer.Deserialize<T>(data);
    }
}