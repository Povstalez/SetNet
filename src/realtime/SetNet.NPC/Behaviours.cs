using System;
using System.Text;
using System.Threading.Tasks;

namespace SetNet.NPC
{
    /// <summary>
    /// A vendor NPC. Interacting (<c>"open"</c>) has <b>no server-side side effect</b> — it just returns a capability
    /// hand-off <c>"vendor:&lt;vendorId&gt;"</c>. The client, seeing the capability, opens its existing vendor UI
    /// (e.g. <c>SetNet.Vendor</c>) against that vendor id. This is the canonical composition pattern: the NPC layer
    /// stays a thin dispatcher and the domain module keeps owning its logic.
    /// </summary>
    public sealed class VendorNpcBehaviour : INpcBehaviour
    {
        private readonly string _vendorId;

        /// <summary>Creates a vendor behaviour that hands off to vendor id <paramref name="vendorId"/> (default = the NPC type).</summary>
        public VendorNpcBehaviour(string? vendorId = null, string npcType = "vendor")
        {
            NpcType = npcType;
            _vendorId = vendorId ?? npcType;
        }

        /// <inheritdoc/>
        public string NpcType { get; }

        /// <inheritdoc/>
        public Task<NpcResponse> OnInteractAsync(NpcContext ctx, NpcInteraction request)
            => Task.FromResult(NpcResponse.Success(
                message: "Welcome to my shop.",
                capability: "vendor:" + _vendorId));

        /// <inheritdoc/>
        public Task OnSpawnAsync(NpcContext ctx) => Task.CompletedTask;
        /// <inheritdoc/>
        public Task OnDespawnAsync(NpcContext ctx) => Task.CompletedTask;
    }

    /// <summary>
    /// The tiny hub contract a <see cref="BufferNpcBehaviour"/> resolves from <see cref="NpcContext.Services"/> to
    /// apply a buff. Kept internal to the NPC package (no hard dependency on any status-effect module): the app either
    /// implements this over its real status-effect hub, or the buffer resolves that hub directly and adapts.
    /// </summary>
    public interface IBuffApplier
    {
        /// <summary>Applies buff <paramref name="buffId"/> to <paramref name="playerKey"/> for <paramref name="durationSeconds"/> (server-authoritative).</summary>
        Task ApplyAsync(string playerKey, string buffId, float durationSeconds);
    }

    /// <summary>
    /// A buffer NPC with an <b>immediate server-side effect</b> and no hand-off. Interacting (<c>"buff"</c>) resolves
    /// an <see cref="IBuffApplier"/> from <see cref="NpcContext.Services"/>, applies the buff, and returns
    /// <c>Ok=true</c> (no <see cref="NpcResponse.Capability"/>). Demonstrates the second behaviour shape — act now,
    /// nothing for the client to open.
    /// </summary>
    public sealed class BufferNpcBehaviour : INpcBehaviour
    {
        private readonly string _buffId;
        private readonly float _durationSeconds;

        /// <summary>Creates a buffer that applies <paramref name="buffId"/> for <paramref name="durationSeconds"/>.</summary>
        public BufferNpcBehaviour(string buffId = "blessing", float durationSeconds = 600f, string npcType = "buffer")
        {
            NpcType = npcType;
            _buffId = buffId;
            _durationSeconds = durationSeconds;
        }

        /// <inheritdoc/>
        public string NpcType { get; }

        /// <inheritdoc/>
        public async Task<NpcResponse> OnInteractAsync(NpcContext ctx, NpcInteraction request)
        {
            var applier = ctx.Services.GetService(typeof(IBuffApplier)) as IBuffApplier;
            if (applier == null)
                return NpcResponse.Fail("No buff applier is configured on the server.");

            await applier.ApplyAsync(ctx.PlayerKey, _buffId, _durationSeconds).ConfigureAwait(false);
            return NpcResponse.Success(message: $"You feel the {_buffId}.");   // no capability — the effect is immediate
        }

        /// <inheritdoc/>
        public Task OnSpawnAsync(NpcContext ctx) => Task.CompletedTask;
        /// <inheritdoc/>
        public Task OnDespawnAsync(NpcContext ctx) => Task.CompletedTask;
    }

    /// <summary>
    /// A teleporter NPC. Interacting (<c>"teleport"</c>, payload = UTF-8 destination zone, or a
    /// <see cref="DefaultDestination"/>) returns a capability hand-off <c>"teleport:&lt;zone&gt;"</c>; the client then
    /// drives the actual migration through its zones layer (e.g. <c>SetNet.Zones</c>). Shows a hand-off that carries a
    /// parameter (the destination) in the capability string.
    /// </summary>
    public sealed class TeleporterNpcBehaviour : INpcBehaviour
    {
        /// <summary>Destination zone used when the interaction payload is empty. Optional.</summary>
        public string? DefaultDestination { get; }

        /// <summary>Creates a teleporter that defaults to <paramref name="defaultDestination"/> when no payload is supplied.</summary>
        public TeleporterNpcBehaviour(string? defaultDestination = null, string npcType = "teleporter")
        {
            NpcType = npcType;
            DefaultDestination = defaultDestination;
        }

        /// <inheritdoc/>
        public string NpcType { get; }

        /// <inheritdoc/>
        public Task<NpcResponse> OnInteractAsync(NpcContext ctx, NpcInteraction request)
        {
            var dest = request.Payload.Length > 0
                ? Encoding.UTF8.GetString(request.Payload)
                : DefaultDestination;

            if (string.IsNullOrEmpty(dest))
                return Task.FromResult(NpcResponse.Fail("No teleport destination specified."));

            return Task.FromResult(NpcResponse.Success(
                message: $"Teleporting to {dest}…",
                capability: "teleport:" + dest));
        }

        /// <inheritdoc/>
        public Task OnSpawnAsync(NpcContext ctx) => Task.CompletedTask;
        /// <inheritdoc/>
        public Task OnDespawnAsync(NpcContext ctx) => Task.CompletedTask;
    }
}
