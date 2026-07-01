using System.Linq;
using SetNet.Compression;
using SetNet.Json;
using SetNet.Messaging;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Round-trip correctness for the serializer adapter packages (Json, and the Compression decorator over it).</summary>
public class SerializerAdaptersTests
{
    public class Dto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int[] Values { get; set; } = System.Array.Empty<int>();
    }

    private static readonly Dto Sample = new Dto { Id = 7, Name = "goblin", Values = Enumerable.Range(0, 500).ToArray() };

    [Fact]
    public void Json_RoundTrips()
    {
        ISerializer s = new JsonNetSerializer();
        var back = s.Deserialize<Dto>(s.Serialize(Sample));
        Assert.Equal(Sample.Id, back.Id);
        Assert.Equal(Sample.Name, back.Name);
        Assert.Equal(Sample.Values, back.Values);
    }

    [Fact]
    public void Compression_RoundTrips_And_Shrinks_Large_Payloads()
    {
        ISerializer inner = new JsonNetSerializer();
        ISerializer compressed = new CompressingSerializer(inner, minBytes: 64);

        var rawSize = inner.Serialize(Sample).Length;
        var compressedBytes = compressed.Serialize(Sample);
        var back = compressed.Deserialize<Dto>(compressedBytes);

        Assert.Equal(Sample.Values, back.Values);                 // correctness
        Assert.True(compressedBytes.Length < rawSize);            // the repetitive payload compresses
    }

    [Fact]
    public void Compression_Leaves_Small_Payloads_Raw()
    {
        ISerializer compressed = new CompressingSerializer(new JsonNetSerializer(), minBytes: 4096);
        var small = new Dto { Id = 1, Name = "x" };
        var back = compressed.Deserialize<Dto>(compressed.Serialize(small));   // below threshold → raw path
        Assert.Equal("x", back.Name);
    }
}
