using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.Mobs
{
    /// <summary>
    /// Client-side mobs driver, attached by <see cref="MobClientExtensions.UseMobs"/>. The client only renders
    /// server-authoritative mob state and sends its own attacks (which the server validates) — no AI or combat math
    /// runs here. Movement/HP/cast state ride whatever replication the app uses (e.g. SetNet.StateSync); these events
    /// carry the discrete cues (spawn/despawn/attack/aggro/death) that a raw snapshot doesn't convey. Rides the unified
    /// protocol on the <see cref="Channels.Mobs"/> channel.
    /// </summary>
    public sealed class MobClient
    {
        private readonly BaseClient _client;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        /// <summary>Raised when a mob becomes visible to this client.</summary>
        public event Action<MobSpawnedInfo>? MobSpawned;

        /// <summary>Raised when a mob leaves this client's view or is destroyed (arg: mob id).</summary>
        public event Action<string>? MobDespawned;

        /// <summary>Raised when a mob ability resolves against targets (for VFX/telegraphs).</summary>
        public event Action<MobAttackInfo>? MobAttackReceived;

        /// <summary>Raised when a mob acquires a target.</summary>
        public event Action<MobAggroInfo>? MobAggro;

        /// <summary>Raised when a mob dies.</summary>
        public event Action<MobDeathInfo>? MobDeath;

        internal MobClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _subscriptions.Add(_client.OnRaw(Channels.Mobs, (ushort)MobsEvt.MobSpawned,
                body => MobSpawned?.Invoke(MobsWire.DecodeSpawn(body))));
            _subscriptions.Add(_client.OnRaw(Channels.Mobs, (ushort)MobsEvt.MobDespawned,
                body => MobDespawned?.Invoke(MobsWire.DecodeId(body))));
            _subscriptions.Add(_client.OnRaw(Channels.Mobs, (ushort)MobsEvt.MobAttack,
                body => MobAttackReceived?.Invoke(MobsWire.DecodeAttackEvent(body))));
            _subscriptions.Add(_client.OnRaw(Channels.Mobs, (ushort)MobsEvt.MobAggro,
                body => MobAggro?.Invoke(MobsWire.DecodeAggro(body))));
            _subscriptions.Add(_client.OnRaw(Channels.Mobs, (ushort)MobsEvt.MobDeath,
                body => MobDeath?.Invoke(MobsWire.DecodeDeath(body))));
        }

        /// <summary>
        /// Sends a player attack against a mob with an ability. The server validates range/cooldown and applies the
        /// damage; throws <see cref="MobException"/> if the attack is rejected (out of range, on cooldown, no such mob).
        /// </summary>
        public async Task SendAttackAsync(string mobId, string abilityId)
        {
            byte[] reply;
            try
            {
                reply = await _client.RequestRawAsync(Channels.Mobs, (ushort)MobsOp.Attack,
                    MobsWire.EncodeAttack(mobId, abilityId)).ConfigureAwait(false);
            }
            catch (ProtocolException ex) { throw new MobException(ex.Message); }
            catch (TimeoutException) { throw new MobException("Attack command timed out."); }

            var (accepted, reason) = MobsWire.DecodeAttackReply(reply);
            if (!accepted) throw new MobException(string.IsNullOrEmpty(reason) ? "Attack rejected." : reason);
        }

        /// <summary>Unsubscribes from all mob events.</summary>
        public void Dispose() { foreach (var s in _subscriptions) s.Dispose(); _subscriptions.Clear(); }
    }

    /// <summary>Attaches a mobs driver to a client by composition — no base class.</summary>
    public static class MobClientExtensions
    {
        /// <summary>Enables client-side mobs and returns the driver (events + <c>SendAttackAsync</c>).</summary>
        public static MobClient UseMobs(this BaseClient client) => new MobClient(client);
    }
}
