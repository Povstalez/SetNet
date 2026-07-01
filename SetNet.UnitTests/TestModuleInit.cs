using System.Runtime.CompilerServices;
using SetNet.Auth;
using SetNet.Matchmaking;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Rooms;
using SetNet.Rpc;

namespace SetNet.UnitTests;

/// <summary>
/// Runs once, before any test, when the test assembly loads. Registers the MessagePack serializer (the core
/// bundles none) and enables RPC so the SetNet.Rpc assembly is loaded before the first handler-discovery scan.
/// </summary>
internal static class TestModuleInit
{
    /// <summary>Runs once, automatically, when the test assembly is loaded.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        SetNetSerializer.Use(new MessagePackNetSerializer());
        RpcRuntime.Enable();
        AuthRuntime.Enable();
        RoomsRuntime.Enable();
        MatchmakingRuntime.Enable();
    }
}
