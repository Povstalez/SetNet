using System.Text.Json;
using SetNet.Messaging;

namespace SetNet.Json
{
    /// <summary>
    /// A <see cref="ISerializer"/> backed by <c>System.Text.Json</c>. Human-readable and web-friendly (great for
    /// debugging or interop with JSON clients), at the cost of larger payloads than a binary format. Register once at
    /// startup with <c>SetNetSerializer.Use(new JsonNetSerializer())</c>; both ends of a connection must use the same
    /// serializer.
    /// </summary>
    public sealed class JsonNetSerializer : ISerializer
    {
        private readonly JsonSerializerOptions _options;

        /// <summary>Creates the serializer, optionally with custom <see cref="JsonSerializerOptions"/> (converters, naming, etc.).</summary>
        public JsonNetSerializer(JsonSerializerOptions? options = null)
            => _options = options ?? new JsonSerializerOptions();

        /// <inheritdoc/>
        public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, _options);

        /// <inheritdoc/>
        public T Deserialize<T>(byte[] data) => JsonSerializer.Deserialize<T>(data, _options)!;
    }
}
