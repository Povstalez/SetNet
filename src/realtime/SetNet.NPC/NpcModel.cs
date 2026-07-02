using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.GeoData;

namespace SetNet.NPC
{
    /// <summary>Thrown when an NPC operation fails (e.g. an interact request times out).</summary>
    public sealed class NpcException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public NpcException(string message) : base(message) { }
    }

    /// <summary>
    /// One spawned NPC in the world. Immutable identity (<see cref="Id"/>/<see cref="Type"/>) plus placement
    /// (<see cref="Position"/>/<see cref="Zone"/>) and opaque display <see cref="Metadata"/> sent to clients; the
    /// server-only <see cref="State"/> bag never leaves the server (daily-stock seeds, cooldowns, …).
    /// </summary>
    public sealed class NpcInstance
    {
        /// <summary>Unique instance id (assigned at spawn).</summary>
        public string Id { get; }

        /// <summary>The behaviour key this instance is driven by (e.g. <c>"blacksmith"</c>), matching an <see cref="INpcBehaviour.NpcType"/>.</summary>
        public string Type { get; }

        /// <summary>World position, used for range checks / interest.</summary>
        public Vec3 Position { get; }

        /// <summary>Owning zone/node id (pairs with sharding/zones).</summary>
        public string Zone { get; }

        /// <summary>Opaque display data shipped to clients (name, model, icon…). Never interpreted by the framework.</summary>
        public byte[] Metadata { get; }

        /// <summary>Server-only scratch state — never sent to clients (daily stock seed, cooldowns, …).</summary>
        public IDictionary<string, object> State { get; } = new Dictionary<string, object>();

        /// <summary>Creates an instance. Called by <see cref="NpcServer.Spawn"/>; you rarely construct these directly.</summary>
        public NpcInstance(string id, string type, Vec3 position, string zone, byte[]? metadata = null)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Type = type ?? throw new ArgumentNullException(nameof(type));
            Position = position;
            Zone = zone ?? "";
            Metadata = metadata ?? Array.Empty<byte>();
        }
    }

    /// <summary>The spawn descriptor passed to <see cref="NpcServer.Spawn"/>.</summary>
    public sealed class NpcSpawn
    {
        /// <summary>The behaviour key (must be <see cref="NpcServer.Register"/>ed). Required.</summary>
        public string Type { get; set; } = "";

        /// <summary>World position.</summary>
        public Vec3 Position { get; set; } = Vec3.Zero;

        /// <summary>Owning zone/node id.</summary>
        public string Zone { get; set; } = "";

        /// <summary>Opaque display data sent to clients (optional).</summary>
        public byte[]? Metadata { get; set; }

        /// <summary>An explicit instance id; if null a new GUID is assigned.</summary>
        public string? Id { get; set; }
    }

    /// <summary>A client → NPC interaction request. <see cref="Action"/> is behaviour-defined; <see cref="Payload"/> carries opaque args.</summary>
    public sealed class NpcInteraction
    {
        /// <summary>The action the player performs (<c>"open"</c>, <c>"talk"</c>, <c>"buff"</c>, …) — the behaviour defines the vocabulary.</summary>
        public string Action { get; }

        /// <summary>Opaque action arguments (a chosen dialogue option id, a quest id, a destination zone…). Never null.</summary>
        public byte[] Payload { get; }

        /// <summary>Creates an interaction.</summary>
        public NpcInteraction(string action, byte[]? payload = null)
        {
            Action = action ?? "";
            Payload = payload ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// An NPC → client interaction result. Besides <see cref="Ok"/>/<see cref="Message"/>/<see cref="Payload"/>, the
    /// optional <see cref="Capability"/> is the composition hand-off hint: a behaviour returns e.g.
    /// <c>"vendor:blacksmith"</c> and the client opens its existing domain UI instead of the NPC layer re-implementing it.
    /// </summary>
    public sealed class NpcResponse
    {
        /// <summary>Whether the interaction succeeded.</summary>
        public bool Ok { get; }

        /// <summary>Human-readable text / error message (never null).</summary>
        public string Message { get; }

        /// <summary>Opaque action result (a dialogue node, a list…). Never null.</summary>
        public byte[] Payload { get; }

        /// <summary>
        /// Optional hand-off hint (e.g. <c>"vendor:blacksmith"</c>, <c>"bank:personal"</c>, <c>"teleport:town"</c>).
        /// Null for behaviours with an immediate server-side effect (a buffer) that need no client follow-up.
        /// </summary>
        public string? Capability { get; }

        /// <summary>Creates a response.</summary>
        public NpcResponse(bool ok, string? message = null, byte[]? payload = null, string? capability = null)
        {
            Ok = ok;
            Message = message ?? "";
            Payload = payload ?? Array.Empty<byte>();
            Capability = capability;
        }

        /// <summary>A successful response, optionally carrying a capability hand-off and/or a result payload.</summary>
        public static NpcResponse Success(string? message = null, byte[]? payload = null, string? capability = null)
            => new NpcResponse(true, message, payload, capability);

        /// <summary>A failed response with an error message.</summary>
        public static NpcResponse Fail(string message) => new NpcResponse(false, message);
    }

    /// <summary>
    /// A behaviour's whole world for one interaction, so behaviours never touch statics: the interacting
    /// <see cref="Peer"/>, its resolved <see cref="PlayerKey"/>, the <see cref="Npc"/> being interacted with, and the
    /// <see cref="Services"/> provider from which to pull whatever hubs the app registered (inventory, wallet, zones…).
    /// </summary>
    public sealed class NpcContext
    {
        /// <summary>The instance being interacted with.</summary>
        public NpcInstance Npc { get; }

        /// <summary>The interacting player's connection.</summary>
        public BasePeer Peer { get; }

        /// <summary>The interacting player's stable key (per the configured resolver).</summary>
        public string PlayerKey { get; }

        /// <summary>The app-supplied service provider (resolve InventoryServer / WalletServer / ZonesServer / … here). Never null.</summary>
        public IServiceProvider Services { get; }

        /// <summary>Creates a context (built by <see cref="NpcServer"/> for each interaction).</summary>
        public NpcContext(NpcInstance npc, BasePeer peer, string playerKey, IServiceProvider services)
        {
            Npc = npc ?? throw new ArgumentNullException(nameof(npc));
            Peer = peer ?? throw new ArgumentNullException(nameof(peer));
            PlayerKey = playerKey ?? "";
            Services = services ?? EmptyServiceProvider.Instance;
        }
    }

    /// <summary>
    /// The one thing a developer writes per NPC type. Everything around it (registration, spawning, interest, the
    /// interact round-trip, range/rate gating) is standardized by the framework, so two NPCs are built the same way —
    /// register a behaviour, spawn instances — regardless of what they do.
    /// </summary>
    public interface INpcBehaviour
    {
        /// <summary>The behaviour key this handles; matches <see cref="NpcInstance.Type"/> / <see cref="NpcSpawn.Type"/>.</summary>
        string NpcType { get; }

        /// <summary>Runs one interaction and returns the result (possibly with a <see cref="NpcResponse.Capability"/> hand-off).</summary>
        Task<NpcResponse> OnInteractAsync(NpcContext ctx, NpcInteraction request);

        /// <summary>Called once when an instance of this type spawns (optional; default no-op via a default interface method).</summary>
        Task OnSpawnAsync(NpcContext ctx) => Task.CompletedTask;

        /// <summary>Called once when an instance of this type despawns (optional; default no-op via a default interface method).</summary>
        Task OnDespawnAsync(NpcContext ctx) => Task.CompletedTask;
    }

    /// <summary>A do-nothing <see cref="IServiceProvider"/> used when the app doesn't supply one.</summary>
    internal sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new EmptyServiceProvider();
        public object? GetService(Type serviceType) => null;
    }
}
