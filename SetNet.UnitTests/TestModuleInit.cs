using System.Runtime.CompilerServices;
using SetNet.Messaging;
using SetNet.MessagePack;

namespace SetNet.UnitTests;

/// <summary>
/// Registers the MessagePack serializer for the whole test assembly before any test runs. The core library
/// bundles no serializer, so without this the harness's MessagePack-based DTOs would fail to (de)serialize.
/// </summary>
internal static class TestModuleInit
{
    /// <summary>Runs once, automatically, when the test assembly is loaded.</summary>
    [ModuleInitializer]
    internal static void Init() => SetNetSerializer.Use(new MessagePackNetSerializer());
}
