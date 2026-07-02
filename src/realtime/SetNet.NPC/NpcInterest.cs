using System;
using SetNet.Core;

namespace SetNet.NPC
{
    /// <summary>
    /// Decides which NPC instances a given player is told about (spawned/despawned pushes). The default
    /// <see cref="AllInterest"/> tells everyone about everything; <see cref="ZoneInterest"/> scopes to the player's
    /// currently-subscribed zone (set via <c>EnterZone</c>/<c>LeaveZone</c>). The client also filters on its own side,
    /// so co-located clients don't cross-talk.
    /// </summary>
    public interface INpcInterest
    {
        /// <summary>Returns true if <paramref name="peer"/> (subscribed to <paramref name="subscribedZone"/>, possibly null) should hear about <paramref name="instance"/>.</summary>
        bool IsInterested(BasePeer peer, string? subscribedZone, NpcInstance instance);
    }

    /// <summary>Interest that tells every player about every instance. Fine for small/single-zone worlds and tests.</summary>
    public sealed class AllInterest : INpcInterest
    {
        /// <summary>A shared instance (the interest check is stateless).</summary>
        public static readonly AllInterest Instance = new AllInterest();

        /// <inheritdoc/>
        public bool IsInterested(BasePeer peer, string? subscribedZone, NpcInstance instance) => true;
    }

    /// <summary>
    /// Interest scoped to the player's subscribed zone: a player hears about an instance only when the instance's
    /// <see cref="NpcInstance.Zone"/> matches the zone the player entered. A player with no subscribed zone hears nothing.
    /// </summary>
    public sealed class ZoneInterest : INpcInterest
    {
        /// <summary>A shared instance (the interest check is stateless).</summary>
        public static readonly ZoneInterest Instance = new ZoneInterest();

        /// <inheritdoc/>
        public bool IsInterested(BasePeer peer, string? subscribedZone, NpcInstance instance)
            => subscribedZone != null && string.Equals(subscribedZone, instance.Zone, StringComparison.Ordinal);
    }

    /// <summary>Settings for the NPC service.</summary>
    public sealed class NpcOptions
    {
        /// <summary>
        /// Maps a connected peer to the stable player key interactions run under. Defaults to the peer's connection
        /// id — override (e.g. to the authenticated account id from <c>SetNet.Auth</c>) so behaviours address a durable
        /// player identity.
        /// </summary>
        public Func<BasePeer, string> PlayerKey { get; set; } = peer => peer.CurrentPeerInfo.Id.ToString();

        /// <summary>The service provider handed to each <see cref="NpcContext"/> so behaviours can resolve the app's hubs. Optional.</summary>
        public IServiceProvider? Services { get; set; }

        /// <summary>Which instances each client is told about (default <see cref="AllInterest"/>; use <see cref="ZoneInterest"/> for larger worlds).</summary>
        public INpcInterest Interest { get; set; } = AllInterest.Instance;

        /// <summary>
        /// Server-side gate consulted before a behaviour runs: given the interacting player key and the instance,
        /// return false to reject (the client gets an <c>Ok=false</c> response). Default: always allow. Put a
        /// distance/LOS/faction check here for anti-cheat.
        /// </summary>
        public Func<string, NpcInstance, bool> CanInteract { get; set; } = (_, __) => true;
    }
}
