using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Auth
{
    /// <summary>
    /// Client-side auth driver, attached by <see cref="AuthClientExtensions.UseAuth(BaseClient, Func{Task{string}})"/>.
    /// It authenticates automatically after every connect and reconnect (via the client's
    /// <see cref="BaseClient.Connected"/> event): a fresh login the first time, a reconnect-token resume afterwards
    /// (falling back to a fresh login token if the session has expired).
    /// </summary>
    public sealed class AuthClient
    {
        private readonly BaseClient _client;
        private readonly Func<Task<string>> _tokenProvider;
        private string? _reconnectToken;
        private TaskCompletionSource<AuthSession> _ready = new TaskCompletionSource<AuthSession>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True once the current connection is authenticated.</summary>
        public bool IsAuthenticated { get; private set; }

        /// <summary>The active session (identity + session id), or null before the first success.</summary>
        public AuthSession? Session { get; private set; }

        /// <summary>Completes with the session on the first successful authentication (await this after connecting).</summary>
        public Task<AuthSession> WhenAuthenticated => _ready.Task;

        /// <summary>Raised on every successful (re)authentication.</summary>
        public event Action<AuthSession>? Authenticated;

        /// <summary>Raised when an authentication attempt fails (bad token, rejected, timeout).</summary>
        public event Action<string>? AuthFailed;

        internal AuthClient(BaseClient client, Func<Task<string>> tokenProvider)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _client.Connected += OnClientConnected;   // auto-(re)authenticate on connect and reconnect
        }

        private void OnClientConnected() => _ = AuthenticateAsync();

        private async Task AuthenticateAsync()
        {
            try
            {
                AuthResponse response;
                if (_reconnectToken != null)
                {
                    response = await SendAsync(AuthKind.Resume, _reconnectToken).ConfigureAwait(false);
                    if (!response.Success)
                    {
                        // Session gone (TTL) — fall back to a fresh login with a (possibly refreshed) token.
                        _reconnectToken = null;
                        response = await SendAsync(AuthKind.Login, await _tokenProvider().ConfigureAwait(false)).ConfigureAwait(false);
                    }
                }
                else
                {
                    response = await SendAsync(AuthKind.Login, await _tokenProvider().ConfigureAwait(false)).ConfigureAwait(false);
                }

                if (response.Success)
                {
                    _reconnectToken = response.ReconnectToken;
                    Session = new AuthSession(response.AccountId, response.SessionId);
                    IsAuthenticated = true;
                    _ready.TrySetResult(Session);
                    Authenticated?.Invoke(Session);
                }
                else
                {
                    IsAuthenticated = false;
                    AuthFailed?.Invoke(response.Error);
                    _ready.TrySetException(new AuthException(response.Error));
                }
            }
            catch (Exception ex)
            {
                IsAuthenticated = false;
                AuthFailed?.Invoke(ex.Message);
                _ready.TrySetException(ex is AuthException ? ex : new AuthException(ex.Message));
            }
        }

        private async Task<AuthResponse> SendAsync(AuthKind kind, string token)
        {
            var correlationId = AuthRegistry.NextId();
            var tcs = new TaskCompletionSource<AuthResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            AuthRegistry.Register(correlationId, tcs);
            try
            {
                var request = new AuthRequest(correlationId, kind, token);
                await _client.SendAsync(AuthTypes.Request, request.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try { return await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new AuthException("Authentication timed out."); }
                }
            }
            finally { AuthRegistry.Remove(correlationId); }
        }
    }

    /// <summary>Attaches auth to a <see cref="BaseClient"/> by composition — no base class.</summary>
    public static class AuthClientExtensions
    {
        /// <summary>
        /// Enables automatic authentication on a client. Call this <b>before</b> <c>ConnectAsync</c>; then
        /// <c>await auth.WhenAuthenticated</c> to know when the first login completes. Re-auth on reconnect is
        /// automatic. <paramref name="tokenProvider"/> returns a (possibly refreshed) login token from your account
        /// backend — it's called only when a fresh login is needed.
        /// </summary>
        public static AuthClient UseAuth(this BaseClient client, Func<Task<string>> tokenProvider)
            => new AuthClient(client, tokenProvider);

        /// <summary>Convenience overload for a fixed login token (e.g. dev/testing).</summary>
        public static AuthClient UseAuth(this BaseClient client, string token)
            => new AuthClient(client, () => Task.FromResult(token));
    }

    /// <summary>
    /// Auto-discovered client handler for the auth response type. Completes the awaiting handshake in
    /// <see cref="AuthRegistry"/> by correlation id. Message type is <c>byte[]</c> (serializer-agnostic).
    /// </summary>
    [MessageHandler(AuthTypes.Response)]
    public sealed class AuthClientHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            var response = AuthResponse.Decode(data);
            AuthRegistry.Complete(response.CorrelationId, response);
            return Task.CompletedTask;
        }
    }
}
