using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Auth
{
    /// <summary>Per-server auth state: the authenticator, session store, options, and which peers are authenticated.</summary>
    internal sealed class AuthServerState
    {
        public IAuthenticator Authenticator = null!;
        public ISessionStore Store = null!;
        public AuthOptions Options = null!;

        // Weak-keyed: an entry auto-clears when the peer is collected after disconnect — no manual cleanup, no leak.
        private readonly ConditionalWeakTable<BasePeer, Session> _authed = new ConditionalWeakTable<BasePeer, Session>();

        public bool IsAuthenticated(BasePeer peer) => _authed.TryGetValue(peer, out _);
        public void MarkAuthenticated(BasePeer peer, Session session) => _authed.AddOrUpdate(peer, session);
    }

    /// <summary>
    /// Server-side auth entry points. Call <see cref="UseAuth"/> once after constructing your server; it installs
    /// the enforced inbound gate (only <see cref="AuthTypes.Request"/> passes until a peer authenticates) and wires
    /// the auto-discovered <see cref="AuthServerHandler"/>. No base class needed.
    /// </summary>
    public static class AuthServer
    {
        private static readonly ConcurrentDictionary<BaseServer, AuthServerState> _servers
            = new ConcurrentDictionary<BaseServer, AuthServerState>();

        // Process-wide background sweep: proactively evicts idle-past-TTL sessions from every store so dead
        // sessions (dropped clients that never reconnect) don't accumulate. Runs for the process lifetime.
        private static readonly Timer _sweepTimer = new Timer(
            _ => { foreach (var state in _servers.Values) { try { _ = state.Store.SweepAsync(); } catch { /* ignore */ } } },
            null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        /// <summary>
        /// Enables enforced authentication on a server: until a peer authenticates, all of its application frames
        /// (regular messages and RPC) are dropped — only the auth handshake gets through. Validate tokens with your
        /// <paramref name="authenticator"/>. Use over TLS so tokens aren't sent in the clear.
        /// </summary>
        /// <param name="server">The server to protect.</param>
        /// <param name="authenticator">Validates login tokens and resolves account identity.</param>
        /// <param name="options">Session TTL and multi-session policy (defaults if null).</param>
        public static void UseAuth(this BaseServer server, IAuthenticator authenticator, AuthOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (authenticator == null) throw new ArgumentNullException(nameof(authenticator));

            var opts = options ?? new AuthOptions();
            var state = new AuthServerState
            {
                Authenticator = authenticator,
                Options = opts,
                Store = opts.SessionStore ?? new MemorySessionStore(opts.SessionTtl)
            };
            _servers[server] = state;

            // Gate: only the auth request passes for an unauthenticated peer; everything else is dropped.
            server.InboundAuthorizer = (peer, type) => type == AuthTypes.Request || state.IsAuthenticated(peer);
        }

        internal static AuthServerState? Get(BaseServer? server)
            => server != null && _servers.TryGetValue(server, out var state) ? state : null;
    }

    /// <summary>
    /// Auto-discovered server handler for the auth request type. Validates a login token (or resumes a session by
    /// reconnect token), applies the multi-session policy, marks the peer authenticated (opening the gate), and
    /// replies with the session + reconnect token. The message type is <c>byte[]</c> so it stays serializer-agnostic.
    /// </summary>
    [MessageHandler(AuthTypes.Request)]
    public sealed class AuthServerHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public async Task HandleAsync(BasePeer peer, byte[] data)
        {
            var request = AuthRequest.Decode(data);
            var state = AuthServer.Get(peer.CurrentPeerInfo.Server);
            AuthResponse response;

            if (state == null)
            {
                response = AuthResponse.Fail(request.CorrelationId, "authentication is not configured on this server");
            }
            else if (request.Kind == AuthKind.Resume)
            {
                var session = await state.Store.ResumeAsync(request.Token, peer.CurrentPeerInfo).ConfigureAwait(false);
                response = session != null
                    ? Accept(state, peer, session, request.CorrelationId)
                    : AuthResponse.Fail(request.CorrelationId, "session expired");
            }
            else // Login
            {
                AuthResult result;
                try { result = await state.Authenticator.AuthenticateAsync(request.Token).ConfigureAwait(false); }
                catch (Exception ex) { result = AuthResult.Fail(ex.Message); }

                if (!result.Success)
                {
                    response = AuthResponse.Fail(request.CorrelationId, result.Error ?? "authentication failed");
                }
                else
                {
                    response = await ApplyPolicyAndCreateAsync(state, peer, result.AccountId, request.CorrelationId).ConfigureAwait(false);
                }
            }

            await peer.SendAsync(AuthTypes.Response, response.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);
        }

        private static async Task<AuthResponse> ApplyPolicyAndCreateAsync(AuthServerState state, BasePeer peer, string accountId, int corr)
        {
            if (state.Options.MultiSession != MultiSessionPolicy.AllowMultiple)
            {
                var existing = await state.Store.SessionsForAccountAsync(accountId).ConfigureAwait(false);
                if (existing.Count > 0)
                {
                    if (state.Options.MultiSession == MultiSessionPolicy.RejectNew)
                        return AuthResponse.Fail(corr, "account already has an active session");

                    // KickExisting: close the previous connection(s) and drop their sessions.
                    foreach (var s in existing)
                    {
                        try { s.LivePeer?.Disconnect(); } catch { /* already gone */ }
                        await state.Store.RemoveAsync(s).ConfigureAwait(false);
                    }
                }
            }

            var session = await state.Store.CreateAsync(accountId, peer.CurrentPeerInfo).ConfigureAwait(false);
            return Accept(state, peer, session, corr);
        }

        private static AuthResponse Accept(AuthServerState state, BasePeer peer, Session session, int corr)
        {
            state.MarkAuthenticated(peer, session);   // opens the gate for this peer
            return AuthResponse.Ok(corr, session.AccountId, session.SessionId, session.ReconnectToken);
        }
    }
}
