using System.Runtime.CompilerServices;
using SetNet.Auth;
using SetNet.Chat;
using SetNet.Fragmentation;
using SetNet.Lockstep;
using SetNet.Matchmaking;
using SetNet.Party;
using SetNet.ProofOfWork;
using SetNet.Relay;
using SetNet.Voice;
using SetNet.Rooms.HostMigration;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Multiplex;
using SetNet.NatPunch;
using SetNet.Rooms;
using SetNet.Rpc;
using SetNet.Sharding;
using SetNet.StateSync;
using SetNet.StateSync.Rpc;
using SetNet.Streams;

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
        StateSyncRuntime.Enable();
        FragmentationRuntime.Enable();
        StateSyncRpcRuntime.Enable();
        ChatRuntime.Enable();
        PartyRuntime.Enable();
        LockstepRuntime.Enable();
        HostMigrationRuntime.Enable();
        ProofOfWorkRuntime.Enable();
        VoiceRuntime.Enable();
        RelayRuntime.Enable();
        NatPunchRuntime.Enable();
        MultiplexRuntime.Enable();
        StreamsRuntime.Enable();
        ShardingRuntime.Enable();
    }
}
