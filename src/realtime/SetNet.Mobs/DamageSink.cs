using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace SetNet.Mobs
{
    /// <summary>
    /// Applies mob-dealt damage to a player. Damage resolution is app-owned because HP models vary wildly — the
    /// framework decides *intent and timing* (which players, how much, when), the game decides *what a hit does*. Put
    /// an implementation in <see cref="MobOptions.Services"/> (resolved by type). When none is present, the framework
    /// falls back to a minimal built-in HP model (<see cref="BuiltInPlayerHp"/>) so Mobs is useful out of the box.
    /// </summary>
    public interface IDamageSink
    {
        /// <summary>Applies <paramref name="amount"/> damage to a player from a mob's ability. <paramref name="effectId"/> may be null.</summary>
        Task ApplyToPlayerAsync(string playerKey, string mobId, string abilityId, double amount, string? effectId);
    }

    /// <summary>
    /// A minimal, in-process player-HP model used when the app supplies no <see cref="IDamageSink"/>. Tracks a health
    /// value per player key so mobs can actually deal damage in demos/tests; a real game overrides this with its own
    /// combat sink.
    /// </summary>
    public sealed class BuiltInPlayerHp : IDamageSink
    {
        private readonly double _maxHp;
        private readonly ConcurrentDictionary<string, double> _hp = new ConcurrentDictionary<string, double>();

        /// <summary>Creates the fallback HP model with a default max HP per player.</summary>
        public BuiltInPlayerHp(double maxHp = 100) => _maxHp = maxHp;

        /// <summary>The current HP of a player (max if unseen).</summary>
        public double HealthOf(string playerKey) => _hp.TryGetValue(playerKey, out var v) ? v : _maxHp;

        /// <inheritdoc/>
        public Task ApplyToPlayerAsync(string playerKey, string mobId, string abilityId, double amount, string? effectId)
        {
            _hp.AddOrUpdate(playerKey, System.Math.Max(0, _maxHp - amount), (_, cur) => System.Math.Max(0, cur - amount));
            return Task.CompletedTask;
        }
    }
}
