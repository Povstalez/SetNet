using System.Runtime.CompilerServices;
using SetNet.Auction;
using SetNet.Auth;
using SetNet.Chat;
using SetNet.Crafting;
using SetNet.Fragmentation;
using SetNet.Guilds;
using SetNet.Inventory;
using SetNet.Lockstep;
using SetNet.LoadBalancer;
using SetNet.Loot;
using SetNet.Mail;
using SetNet.Marketplace;
using SetNet.Progression;
using SetNet.Quests;
using SetNet.StatusEffects;
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
using SetNet.Trade;
using SetNet.Vendor;
using SetNet.Wallet;
using SetNet.Zones;

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
        InventoryRuntime.Enable();
        TradeRuntime.Enable();
        MailRuntime.Enable();
        ZonesRuntime.Enable();
        WalletRuntime.Enable();
        VendorRuntime.Enable();
        AuctionRuntime.Enable();
        CraftingRuntime.Enable();
        LootRuntime.Enable();
        QuestRuntime.Enable();
        ProgressionRuntime.Enable();
        GuildRuntime.Enable();
        LoadBalancerRuntime.Enable();
        MarketplaceRuntime.Enable();
        StatusEffectRuntime.Enable();
    }
}
