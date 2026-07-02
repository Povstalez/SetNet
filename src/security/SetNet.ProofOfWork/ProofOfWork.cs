using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.ProofOfWork
{
    /// <summary>Reserved wire types for the proof-of-work handshake (below the cluster/voice range). Don't reuse.</summary>
    public static class PoWTypes
    {
        /// <summary>Server → client: a challenge (difficulty + random bytes) the client must solve.</summary>
        public const ushort Challenge = ushort.MaxValue - 28;   // 65507

        /// <summary>Client → server: the nonce that solves the challenge.</summary>
        public const ushort Solution = ushort.MaxValue - 29;    // 65506
    }

    internal static class PoWHash
    {
        // How many leading zero BITS the SHA-256 of (challenge || nonce) must have.
        public static int LeadingZeroBits(byte[] hash)
        {
            var bits = 0;
            foreach (var b in hash)
            {
                if (b == 0) { bits += 8; continue; }
                for (var mask = 7; mask >= 0; mask--)
                {
                    if ((b & (1 << mask)) == 0) bits++;
                    else return bits;
                }
            }
            return bits;
        }

        public static byte[] Hash(SHA256 sha, byte[] challenge, ulong nonce)
        {
            var buf = new byte[challenge.Length + 8];
            Buffer.BlockCopy(challenge, 0, buf, 0, challenge.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(challenge.Length, 8), nonce);
            return sha.ComputeHash(buf);
        }
    }

    /// <summary>Per-peer PoW state on the server: the issued challenge and whether it's been solved.</summary>
    internal sealed class PoWServerState
    {
        public int Difficulty;
        public readonly ConditionalWeakTable<BasePeer, PoWEntry> Peers = new ConditionalWeakTable<BasePeer, PoWEntry>();
        public bool IsSolved(BasePeer peer) => Peers.TryGetValue(peer, out var e) && e.Solved;
    }

    internal sealed class PoWEntry { public byte[] Challenge = Array.Empty<byte>(); public volatile bool Solved; }

    /// <summary>
    /// Server-side proof-of-work admission gate. On connect the server issues a hashcash challenge; until the peer sends a
    /// nonce whose SHA-256(challenge‖nonce) has enough leading zero bits, its application frames are **dropped** (only the
    /// PoW solution passes). This makes mass/bot connections costly (each must burn CPU) without troubling real clients.
    /// </summary>
    public static class ProofOfWorkServer
    {
        private static readonly ConcurrentDictionary<BaseServer, PoWServerState> Servers = new ConcurrentDictionary<BaseServer, PoWServerState>();

        /// <summary>Enables the PoW gate. <paramref name="difficulty"/> is the required leading-zero bits (≈ 2^difficulty hashes; ~18–22 is a good range).</summary>
        public static void UseProofOfWork(this BaseServer server, int difficulty = 20)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            var state = new PoWServerState { Difficulty = difficulty };
            Servers[server] = state;

            // Gate (chained): only the PoW solution or a solved peer may send application frames.
            var previous = server.InboundAuthorizer;
            server.InboundAuthorizer = (peer, type) =>
                (previous == null || previous(peer, type)) && (type == PoWTypes.Solution || state.IsSolved(peer));

            server.PeerConnected += peer => IssueChallenge(state, peer);
        }

        private static void IssueChallenge(PoWServerState state, BasePeer peer)
        {
            var challenge = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(challenge);
            state.Peers.AddOrUpdate(peer, new PoWEntry { Challenge = challenge });

            var frame = new byte[1 + challenge.Length];
            frame[0] = (byte)state.Difficulty;
            Buffer.BlockCopy(challenge, 0, frame, 1, challenge.Length);
            _ = peer.SendAsync(PoWTypes.Challenge, frame, DeliveryMethod.Reliable);
        }

        internal static void OnSolution(BasePeer peer, ulong nonce)
        {
            var state = peer.CurrentPeerInfo.Server != null && Servers.TryGetValue(peer.CurrentPeerInfo.Server, out var s) ? s : null;
            if (state == null || !state.Peers.TryGetValue(peer, out var entry) || entry.Solved) return;

            using var sha = SHA256.Create();
            var hash = PoWHash.Hash(sha, entry.Challenge, nonce);
            if (PoWHash.LeadingZeroBits(hash) >= state.Difficulty) entry.Solved = true;
        }
    }

    /// <summary>Client side: automatically solves the server's PoW challenge on connect and submits the nonce.</summary>
    public static class ProofOfWorkClient
    {
        private static readonly ConcurrentDictionary<BaseClient, byte> Clients = new ConcurrentDictionary<BaseClient, byte>();

        /// <summary>Enables automatic PoW solving on this client. Call once after constructing the client.</summary>
        public static void UseProofOfWork(this BaseClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            Clients[client] = 0;
        }

        internal static void OnChallenge(int difficulty, byte[] challenge)
        {
            // Solve off the receive thread (hashing can take a moment), then submit to every registered client.
            _ = Task.Run(() =>
            {
                var nonce = Solve(difficulty, challenge);
                var frame = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(frame, nonce);
                foreach (var client in Clients.Keys)
                    _ = TrySend(client, frame);
            });
        }

        private static ulong Solve(int difficulty, byte[] challenge)
        {
            using var sha = SHA256.Create();
            for (ulong nonce = 0; nonce < ulong.MaxValue; nonce++)
                if (PoWHash.LeadingZeroBits(PoWHash.Hash(sha, challenge, nonce)) >= difficulty)
                    return nonce;
            return 0;
        }

        private static async Task TrySend(BaseClient client, byte[] frame)
        {
            try { await client.SendAsync(PoWTypes.Solution, frame, DeliveryMethod.Reliable).ConfigureAwait(false); } catch { /* dropping */ }
        }
    }

    /// <summary>Auto-discovered server handler for PoW solutions.</summary>
    [MessageHandler(PoWTypes.Solution)]
    public sealed class PoWSolutionHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            if (data.Length >= 8) ProofOfWorkServer.OnSolution(peer, BinaryPrimitives.ReadUInt64LittleEndian(data));
            return Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for PoW challenges.</summary>
    [MessageHandler(PoWTypes.Challenge)]
    public sealed class PoWChallengeHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            if (data.Length >= 1)
            {
                var difficulty = data[0];
                var challenge = new byte[data.Length - 1];
                Buffer.BlockCopy(data, 1, challenge, 0, challenge.Length);
                ProofOfWorkClient.OnChallenge(difficulty, challenge);
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>One-time bootstrap so the PoW handlers are discovered. Call at startup.</summary>
    public static class ProofOfWorkRuntime
    {
        /// <summary>Ensures the proof-of-work layer is discoverable.</summary>
        public static void Enable() { _ = PoWTypes.Challenge; }
    }
}
