using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.LoginServer
{
    /// <summary>Login channel operations (client → login node).</summary>
    internal enum LoginOp : ushort { Login = 1, ServerList = 2, Select = 3 }

    /// <summary>The outcome of a login attempt, as seen by the client.</summary>
    public enum LoginStatus
    {
        /// <summary>Credentials accepted.</summary>
        Ok,
        /// <summary>Unknown user or wrong password.</summary>
        InvalidCredentials,
        /// <summary>Account is banned.</summary>
        Banned,
        /// <summary>The login node hit an internal error.</summary>
        ServerError
    }

    /// <summary>What the app's authenticator returns to the login node (usually wired to <c>SetNet.Accounts</c>).</summary>
    public sealed class LoginAuth
    {
        /// <summary>True when the credentials are valid and the account may log in.</summary>
        public bool Ok { get; set; }
        /// <summary>True when the account exists but is banned.</summary>
        public bool Banned { get; set; }
        /// <summary>The authenticated account id (used to bind the session token).</summary>
        public string AccountId { get; set; } = "";
        /// <summary>Optional message shown to the client.</summary>
        public string? Message { get; set; }

        /// <summary>A successful auth result.</summary>
        public static LoginAuth Success(string accountId, string? message = null) => new LoginAuth { Ok = true, AccountId = accountId, Message = message };
        /// <summary>A rejected auth result.</summary>
        public static LoginAuth Reject(string? message = null) => new LoginAuth { Ok = false, Message = message };
        /// <summary>A banned auth result.</summary>
        public static LoginAuth Ban(string accountId, string? message = null) => new LoginAuth { Ok = false, Banned = true, AccountId = accountId, Message = message };
    }

    /// <summary>The client-facing result of <see cref="LoginClient.LoginAsync"/>.</summary>
    public sealed class LoginResult
    {
        /// <summary>The status.</summary>
        public LoginStatus Status { get; set; }
        /// <summary>Optional message.</summary>
        public string Message { get; set; } = "";
        /// <summary>True when <see cref="Status"/> is <see cref="LoginStatus.Ok"/>.</summary>
        public bool Ok => Status == LoginStatus.Ok;
    }

    /// <summary>One game server in the list the login node advertises.</summary>
    public sealed class GameServerInfo
    {
        /// <summary>Stable server id.</summary>
        public string Id { get; set; } = "";
        /// <summary>Display name.</summary>
        public string Name { get; set; } = "";
        /// <summary>Host the client connects to.</summary>
        public string Host { get; set; } = "";
        /// <summary>Port the client connects to.</summary>
        public int Port { get; set; }
        /// <summary>Current online player count.</summary>
        public int Online { get; set; }
        /// <summary>Max capacity (0 = unknown/unlimited).</summary>
        public int Max { get; set; }
        /// <summary>Free-form status ("good"/"busy"/"full"/"down"…).</summary>
        public string Status { get; set; } = "good";
    }

    /// <summary>The result of <see cref="LoginClient.SelectServerAsync"/>: a one-time token + where to connect.</summary>
    public sealed class SelectResult
    {
        /// <summary>True when a token was issued.</summary>
        public bool Ok { get; set; }
        /// <summary>The one-time session token to present to the game server.</summary>
        public string Token { get; set; } = "";
        /// <summary>The game server host.</summary>
        public string Host { get; set; } = "";
        /// <summary>The game server port.</summary>
        public int Port { get; set; }
        /// <summary>Optional message on failure.</summary>
        public string Message { get; set; } = "";
    }

    /// <summary>A consumed login token: which account, for which server.</summary>
    public sealed class LoginToken
    {
        /// <summary>The account the token was issued for.</summary>
        public string AccountId { get; set; } = "";
        /// <summary>The server the token is valid for.</summary>
        public string ServerId { get; set; } = "";
    }

    /// <summary>
    /// Issues and consumes one-time login tokens. The login node issues; the <b>game</b> server consumes — so in a real
    /// cluster this must be a <b>shared</b> store (Redis/DB). The default <see cref="MemoryLoginTokenStore"/> is in-process
    /// (co-located / tests only).
    /// </summary>
    public interface ILoginTokenStore
    {
        /// <summary>Issues a token bound to (account, server) with a TTL; returns the token string.</summary>
        Task<string> IssueAsync(string accountId, string serverId, int ttlSeconds);
        /// <summary>Consumes a token once: returns its binding, or null if unknown/expired/already used.</summary>
        Task<LoginToken?> ConsumeAsync(string token);
    }

    /// <summary>In-process one-time token store (co-located / tests). Use a Redis/DB implementation across nodes.</summary>
    public sealed class MemoryLoginTokenStore : ILoginTokenStore
    {
        private readonly ConcurrentDictionary<string, (LoginToken token, long expUnix)> _map = new ConcurrentDictionary<string, (LoginToken, long)>();

        /// <inheritdoc/>
        public Task<string> IssueAsync(string accountId, string serverId, int ttlSeconds)
        {
            var token = Guid.NewGuid().ToString("N");
            var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(1, ttlSeconds);
            _map[token] = (new LoginToken { AccountId = accountId, ServerId = serverId }, exp);
            return Task.FromResult(token);
        }

        /// <inheritdoc/>
        public Task<LoginToken?> ConsumeAsync(string token)
        {
            if (token != null && _map.TryRemove(token, out var e) && DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= e.expUnix)
                return Task.FromResult<LoginToken?>(e.token);
            return Task.FromResult<LoginToken?>(null);
        }
    }

    /// <summary>Options for <see cref="LoginServer"/>.</summary>
    public sealed class LoginOptions
    {
        /// <summary>Verifies credentials — wire this to <c>SetNet.Accounts</c> (or any store). Required.</summary>
        public Func<string, string, Task<LoginAuth>> Authenticate { get; set; } = (_, __) => Task.FromResult(LoginAuth.Reject("no authenticator configured"));
        /// <summary>Supplies the current game-server list — feed it from <c>SetNet.LoadBalancer</c> / your config. Required.</summary>
        public Func<IEnumerable<GameServerInfo>> Servers { get; set; } = Array.Empty<GameServerInfo>;
        /// <summary>Where one-time tokens live (share this instance with your game server). Default in-process.</summary>
        public ILoginTokenStore Tokens { get; set; } = new MemoryLoginTokenStore();
        /// <summary>Token time-to-live in seconds. Default 60.</summary>
        public int TokenTtlSeconds { get; set; } = 60;
    }

    /// <summary>
    /// The login coordinator on a dedicated login node. It authenticates the account, advertises the game-server list, and
    /// issues a one-time session token the client presents to the chosen game server (which validates it against the shared
    /// <see cref="ILoginTokenStore"/>). Enable with <see cref="LoginServerExtensions.UseLoginServer"/>.
    /// </summary>
    public sealed class LoginServer
    {
        private static readonly ConcurrentDictionary<BaseServer, LoginServer> Servers = new ConcurrentDictionary<BaseServer, LoginServer>();

        private readonly LoginOptions _options;
        // Peers that have authenticated on this login node → their account id (needed by Select).
        private readonly ConcurrentDictionary<BasePeer, string> _authed = new ConcurrentDictionary<BasePeer, string>();

        internal LoginServer(LoginOptions options) => _options = options;

        internal static LoginServer Enable(BaseServer server, LoginOptions options)
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new LoginServer(options);
                s.PeerDisconnected += peer => hub._authed.TryRemove(peer, out _);
                return hub;
            });

        internal static LoginServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>The token store this login node issues into (share it with the game server to consume).</summary>
        public ILoginTokenStore Tokens => _options.Tokens;

        internal async Task HandleLoginAsync(ChannelRequest request)
        {
            var (user, pass) = LoginCodec.ReadLoginRequest(request.RawBody);
            LoginAuth auth;
            try { auth = await _options.Authenticate(user, pass).ConfigureAwait(false); }
            catch { auth = LoginAuth.Reject("authentication error"); await ReplyLogin(request, LoginStatus.ServerError, "authentication error"); return; }

            if (auth.Ok)
            {
                _authed[request.Peer] = auth.AccountId;
                await ReplyLogin(request, LoginStatus.Ok, auth.Message ?? "");
            }
            else if (auth.Banned)
                await ReplyLogin(request, LoginStatus.Banned, auth.Message ?? "banned");
            else
                await ReplyLogin(request, LoginStatus.InvalidCredentials, auth.Message ?? "invalid credentials");
        }

        private static Task ReplyLogin(ChannelRequest request, LoginStatus status, string message)
            => request.ReplyRawAsync(LoginCodec.LoginResult(status, message));

        internal Task HandleServerListAsync(ChannelRequest request)
        {
            var servers = new List<GameServerInfo>(_options.Servers() ?? Array.Empty<GameServerInfo>());
            return request.ReplyRawAsync(LoginCodec.ServerList(servers));
        }

        internal async Task HandleSelectAsync(ChannelRequest request)
        {
            var serverId = LoginCodec.ReadSelectRequest(request.RawBody);
            if (!_authed.TryGetValue(request.Peer, out var accountId))
            {
                await request.ReplyRawAsync(LoginCodec.SelectResult(false, "", "", 0, "not logged in")).ConfigureAwait(false);
                return;
            }

            GameServerInfo? server = null;
            foreach (var s in _options.Servers() ?? Array.Empty<GameServerInfo>())
                if (s.Id == serverId) { server = s; break; }

            if (server == null)
            {
                await request.ReplyRawAsync(LoginCodec.SelectResult(false, "", "", 0, "unknown server")).ConfigureAwait(false);
                return;
            }
            if (server.Max > 0 && server.Online >= server.Max)
            {
                await request.ReplyRawAsync(LoginCodec.SelectResult(false, "", "", 0, "server full")).ConfigureAwait(false);
                return;
            }

            var token = await _options.Tokens.IssueAsync(accountId, serverId, _options.TokenTtlSeconds).ConfigureAwait(false);
            await request.ReplyRawAsync(LoginCodec.SelectResult(true, token, server.Host, server.Port, "")).ConfigureAwait(false);
        }
    }

    /// <summary>Auto-discovered channel service for the login coordinator.</summary>
    [ProtocolChannel(Channels.Login)]
    public sealed class LoginChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = LoginServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("login server is not configured on this node");
            return request.Op switch
            {
                (ushort)LoginOp.Login => hub.HandleLoginAsync(request),
                (ushort)LoginOp.ServerList => hub.HandleServerListAsync(request),
                (ushort)LoginOp.Select => hub.HandleSelectAsync(request),
                _ => throw new ProtocolException($"unknown login op {request.Op}"),
            };
        }
    }

    /// <summary>Client-side login driver (from <see cref="LoginClientExtensions.UseLogin"/>).</summary>
    public sealed class LoginClient
    {
        private readonly BaseClient _client;
        internal LoginClient(BaseClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>Authenticates the account with the login node.</summary>
        public async Task<LoginResult> LoginAsync(string username, string password)
        {
            var body = await _client.RequestRawAsync(Channels.Login, (ushort)LoginOp.Login,
                LoginCodec.LoginRequest(username, password)).ConfigureAwait(false);
            return LoginCodec.ReadLoginResult(body);
        }

        /// <summary>Fetches the advertised game-server list.</summary>
        public async Task<IReadOnlyList<GameServerInfo>> ServerListAsync()
        {
            var body = await _client.RequestRawAsync(Channels.Login, (ushort)LoginOp.ServerList, Array.Empty<byte>()).ConfigureAwait(false);
            return LoginCodec.ReadServerList(body);
        }

        /// <summary>Selects a server; returns a one-time token + where to connect (must be logged in first).</summary>
        public async Task<SelectResult> SelectServerAsync(string serverId)
        {
            var body = await _client.RequestRawAsync(Channels.Login, (ushort)LoginOp.Select,
                LoginCodec.SelectRequest(serverId)).ConfigureAwait(false);
            return LoginCodec.ReadSelectResult(body);
        }
    }

    /// <summary>Enables the login coordinator on a login node.</summary>
    public static class LoginServerExtensions
    {
        /// <summary>Attaches the login coordinator; returns it (exposes <c>Tokens</c> to share with the game server).</summary>
        public static LoginServer UseLoginServer(this BaseServer server, LoginOptions options)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (options == null) throw new ArgumentNullException(nameof(options));
            return LoginServer.Enable(server, options);
        }
    }

    /// <summary>Attaches a login driver to a client.</summary>
    public static class LoginClientExtensions
    {
        /// <summary>Enables client-side login; returns the driver (<c>LoginAsync</c>/<c>ServerListAsync</c>/<c>SelectServerAsync</c>).</summary>
        public static LoginClient UseLogin(this BaseClient client) => new LoginClient(client);
    }

    /// <summary>One-time bootstrap so the login channel service is discovered. Call at startup on the login node.</summary>
    public static class LoginRuntime
    {
        /// <summary>Ensures the login layer is discoverable.</summary>
        public static void Enable() { _ = typeof(LoginChannelService); }
    }

    /// <summary>Hand-framed, serializer-agnostic codec for the login control frames.</summary>
    internal static class LoginCodec
    {
        private static byte[] Encode(Action<BinaryWriter> write)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) write(w);
            return ms.ToArray();
        }

        private static T Decode<T>(byte[] body, Func<BinaryReader, T> read)
        {
            using var ms = new MemoryStream(body ?? Array.Empty<byte>());
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return read(r);
        }

        public static byte[] LoginRequest(string user, string pass) => Encode(w => { w.Write(user ?? ""); w.Write(pass ?? ""); });
        public static (string user, string pass) ReadLoginRequest(byte[] b) => Decode(b, r => (r.ReadString(), r.ReadString()));

        public static byte[] LoginResult(LoginStatus status, string message) => Encode(w => { w.Write((byte)status); w.Write(message ?? ""); });
        public static LoginResult ReadLoginResult(byte[] b) => Decode(b, r => new LoginResult { Status = (LoginStatus)r.ReadByte(), Message = r.ReadString() });

        public static byte[] ServerList(IReadOnlyList<GameServerInfo> servers) => Encode(w =>
        {
            w.Write(servers.Count);
            foreach (var s in servers)
            {
                w.Write(s.Id ?? ""); w.Write(s.Name ?? ""); w.Write(s.Host ?? "");
                w.Write(s.Port); w.Write(s.Online); w.Write(s.Max); w.Write(s.Status ?? "");
            }
        });

        public static List<GameServerInfo> ReadServerList(byte[] b) => Decode(b, r =>
        {
            var count = r.ReadInt32();
            var list = new List<GameServerInfo>(count);
            for (var i = 0; i < count; i++)
                list.Add(new GameServerInfo
                {
                    Id = r.ReadString(), Name = r.ReadString(), Host = r.ReadString(),
                    Port = r.ReadInt32(), Online = r.ReadInt32(), Max = r.ReadInt32(), Status = r.ReadString(),
                });
            return list;
        });

        public static byte[] SelectRequest(string serverId) => Encode(w => w.Write(serverId ?? ""));
        public static string ReadSelectRequest(byte[] b) => Decode(b, r => r.ReadString());

        public static byte[] SelectResult(bool ok, string token, string host, int port, string message) =>
            Encode(w => { w.Write(ok); w.Write(token ?? ""); w.Write(host ?? ""); w.Write(port); w.Write(message ?? ""); });

        public static SelectResult ReadSelectResult(byte[] b) => Decode(b, r => new SelectResult
        {
            Ok = r.ReadBoolean(), Token = r.ReadString(), Host = r.ReadString(), Port = r.ReadInt32(), Message = r.ReadString(),
        });
    }
}
