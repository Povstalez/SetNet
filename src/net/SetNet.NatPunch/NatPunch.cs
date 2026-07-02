using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.NatPunch
{
    /// <summary>Reserved wire types for the NAT punch-through coordinator. Don't reuse these ids for application messages.</summary>
    public static class NatPunchTypes
    {
        /// <summary>Client → server: register/punch/cancel command.</summary>
        public const ushort Command = ushort.MaxValue - 38;   // 65497

        /// <summary>Server → client: correlated reply to a register/punch/cancel command.</summary>
        public const ushort Reply = ushort.MaxValue - 39;     // 65496

        /// <summary>Server → client: push event carrying the counterpart's endpoint candidates.</summary>
        public const ushort Event = ushort.MaxValue - 40;     // 65495
    }

    internal enum NatPunchOp : byte { Register = 0, Punch = 1, Cancel = 2 }

    /// <summary>Thrown when a NAT punch register/punch command fails (unknown code, timeout).</summary>
    public sealed class NatPunchException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public NatPunchException(string message) : base(message) { }
    }

    /// <summary>
    /// The endpoint candidates of the counterpart peer, handed to both sides by the coordinator. Feed this to
    /// <see cref="NatPuncher.TryPunchAsync"/> to attempt the actual UDP hole punch.
    /// </summary>
    public sealed class NatPunchTarget
    {
        /// <summary>The counterpart's public (server-observed) endpoint candidate: its observed source IP + its reported UDP port. Null when the coordinator couldn't observe one.</summary>
        public IPEndPoint? PublicEndPoint { get; set; }

        /// <summary>The counterpart's private (LAN) endpoint candidates, for peers behind the same NAT.</summary>
        public IReadOnlyList<IPEndPoint> PrivateEndPoints { get; set; } = Array.Empty<IPEndPoint>();

        /// <summary>True when this side registered the session (the "host"); false for the joining side.</summary>
        public bool IsHost { get; set; }
    }

    // ---- wire ----

    internal sealed class NatPunchCommand
    {
        public int CorrelationId;
        public NatPunchOp Op;
        public string Code = "";
        public int UdpPort;
        public string[] PrivateEndPoints = Array.Empty<string>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId);
            w.Write((byte)Op);
            w.Write(Code ?? "");
            w.Write(UdpPort);
            w.Write(PrivateEndPoints.Length);
            foreach (var ep in PrivateEndPoints) w.Write(ep ?? "");
            return ms.ToArray();
        }

        public static NatPunchCommand Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var cmd = new NatPunchCommand
            {
                CorrelationId = r.ReadInt32(),
                Op = (NatPunchOp)r.ReadByte(),
                Code = r.ReadString(),
                UdpPort = r.ReadInt32(),
            };
            var count = r.ReadInt32();
            cmd.PrivateEndPoints = new string[count];
            for (var i = 0; i < count; i++) cmd.PrivateEndPoints[i] = r.ReadString();
            return cmd;
        }
    }

    internal sealed class NatPunchReply
    {
        public int CorrelationId;
        public bool Success;
        public string Error = "";
        public string Code = "";

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId);
            w.Write(Success);
            w.Write(Error ?? "");
            w.Write(Code ?? "");
            return ms.ToArray();
        }

        public static NatPunchReply Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new NatPunchReply
            {
                CorrelationId = r.ReadInt32(),
                Success = r.ReadBoolean(),
                Error = r.ReadString(),
                Code = r.ReadString(),
            };
        }
    }

    internal sealed class NatPunchEvent
    {
        public string Code = "";
        public bool IsHost;
        public string PublicEndPoint = "";
        public string[] PrivateEndPoints = Array.Empty<string>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(Code ?? "");
            w.Write(IsHost);
            w.Write(PublicEndPoint ?? "");
            w.Write(PrivateEndPoints.Length);
            foreach (var ep in PrivateEndPoints) w.Write(ep ?? "");
            return ms.ToArray();
        }

        public static NatPunchEvent Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var evt = new NatPunchEvent
            {
                Code = r.ReadString(),
                IsHost = r.ReadBoolean(),
                PublicEndPoint = r.ReadString(),
            };
            var count = r.ReadInt32();
            evt.PrivateEndPoints = new string[count];
            for (var i = 0; i < count; i++) evt.PrivateEndPoints[i] = r.ReadString();
            return evt;
        }
    }

    // ---- shared parsing helpers ----

    internal static class EndPointCodec
    {
        /// <summary>Formats an endpoint as "ip:port" (IPv6 uses "[ip]:port").</summary>
        public static string Format(IPEndPoint ep)
            => ep.Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ep.Address}]:{ep.Port}" : $"{ep.Address}:{ep.Port}";

        /// <summary>Parses "ip:port" / "[ip]:port" back into an endpoint; null when malformed.</summary>
        public static IPEndPoint? Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var idx = s.LastIndexOf(':');
            if (idx <= 0 || idx == s.Length - 1) return null;
            var host = s.Substring(0, idx).Trim('[', ']');
            if (!IPAddress.TryParse(host, out var addr)) return null;
            if (!int.TryParse(s.Substring(idx + 1), out var port) || port < 1 || port > ushort.MaxValue) return null;
            return new IPEndPoint(addr, port);
        }

        public static NatPunchTarget ToTarget(NatPunchEvent evt)
        {
            var privates = new List<IPEndPoint>();
            foreach (var p in evt.PrivateEndPoints)
            {
                var parsed = Parse(p);
                if (parsed != null) privates.Add(parsed);
            }
            return new NatPunchTarget
            {
                PublicEndPoint = Parse(evt.PublicEndPoint),
                PrivateEndPoints = privates,
                IsHost = evt.IsHost,
            };
        }
    }

    // ---- client-side plumbing ----

    internal static class NatPunchRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<NatPunchReply>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<NatPunchReply>>();
        private static readonly ConcurrentDictionary<NatPunchClient, byte> Clients
            = new ConcurrentDictionary<NatPunchClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<NatPunchReply> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, NatPunchReply reply) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(reply); }
        public static void RegisterClient(NatPunchClient c) => Clients[c] = 0;
        public static void DispatchEvent(NatPunchEvent evt) { foreach (var c in Clients.Keys) c.OnEvent(evt); }
    }

    /// <summary>
    /// Client-side NAT punch-through driver, attached by <see cref="NatPunchClientExtensions.UseNatPunch"/>.
    /// The host registers a punch session and shares the returned code out of band (chat, lobby, invite); the guest
    /// calls <see cref="PunchAsync"/> with that code. The coordinator then pushes each side the other's endpoint
    /// candidates and both run <see cref="NatPuncher.TryPunchAsync"/> simultaneously to open the UDP path.
    /// <para>
    /// The public candidate is the server-observed source IP plus the client-reported UDP port, which works for
    /// full-cone / port-preserving NATs (the common home-router case). Symmetric NATs randomize ports per
    /// destination and will fail to punch — fall back to <c>SetNet.Relay</c> in that case.
    /// </para>
    /// </summary>
    public sealed class NatPunchClient
    {
        private readonly BaseClient _client;
        private readonly object _gate = new object();
        private string? _code;
        private bool _isHost;
        private TaskCompletionSource<NatPunchTarget>? _waitingTarget;

        /// <summary>The code of the session this client registered or punched, or null.</summary>
        public string? Code { get { lock (_gate) return _code; } }

        /// <summary>Raised whenever the coordinator hands us a counterpart's endpoint candidates (hosts get one per guest).</summary>
        public event Action<NatPunchTarget>? TargetReceived;

        internal NatPunchClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            NatPunchRegistry.RegisterClient(this);
        }

        /// <summary>
        /// Registers a punch session as the host; returns the code guests use. <paramref name="udpPort"/> is the local
        /// UDP port this host will punch from (the same one later given to <see cref="NatPuncher.TryPunchAsync"/>).
        /// </summary>
        public async Task<string> RegisterAsync(int udpPort)
        {
            var reply = await SendCommand(NatPunchOp.Register, "", udpPort).ConfigureAwait(false);
            if (!reply.Success) throw new NatPunchException(reply.Error);
            lock (_gate) { _code = reply.Code; _isHost = true; }
            return reply.Code;
        }

        /// <summary>
        /// Requests a punch against a registered session as the guest and waits for the coordinator to hand back the
        /// host's endpoint candidates. <paramref name="udpPort"/> is the local UDP port this guest will punch from.
        /// </summary>
        public async Task<NatPunchTarget> PunchAsync(string code, int udpPort, int timeoutMs = 10_000)
        {
            var tcs = new TaskCompletionSource<NatPunchTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) { _code = code; _isHost = false; _waitingTarget = tcs; }

            var reply = await SendCommand(NatPunchOp.Punch, code, udpPort).ConfigureAwait(false);
            if (!reply.Success) { lock (_gate) _waitingTarget = null; throw new NatPunchException(reply.Error); }

            using var timeout = new CancellationTokenSource(timeoutMs);
            using (timeout.Token.Register(() => tcs.TrySetCanceled()))
            {
                try { return await tcs.Task.ConfigureAwait(false); }
                catch (OperationCanceledException) { throw new NatPunchException("Timed out waiting for the punch target."); }
                finally { lock (_gate) _waitingTarget = null; }
            }
        }

        /// <summary>Unregisters this host's session. Tolerant of a dropped connection (the server auto-removes it).</summary>
        public async Task CancelAsync()
        {
            try { await SendCommand(NatPunchOp.Cancel, "", 0).ConfigureAwait(false); }
            catch { /* already disconnected — server cleans up */ }
            lock (_gate) _code = null;
        }

        /// <summary>Convenience for hosts: awaits the next guest's endpoint candidates (one <see cref="TargetReceived"/>).</summary>
        public async Task<NatPunchTarget> WaitForGuestAsync(int timeoutMs = 60_000)
        {
            var tcs = new TaskCompletionSource<NatPunchTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(NatPunchTarget t) => tcs.TrySetResult(t);
            TargetReceived += Handler;
            try
            {
                using var timeout = new CancellationTokenSource(timeoutMs);
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try { return await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new NatPunchException("Timed out waiting for a guest."); }
                }
            }
            finally { TargetReceived -= Handler; }
        }

        private async Task<NatPunchReply> SendCommand(NatPunchOp op, string code, int udpPort)
        {
            var id = NatPunchRegistry.NextId();
            var tcs = new TaskCompletionSource<NatPunchReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            NatPunchRegistry.Register(id, tcs);
            try
            {
                var cmd = new NatPunchCommand
                {
                    CorrelationId = id,
                    Op = op,
                    Code = code,
                    UdpPort = udpPort,
                    PrivateEndPoints = udpPort > 0 ? NatPuncher.GetPrivateEndPoints(udpPort) : Array.Empty<string>(),
                };
                await _client.SendAsync(NatPunchTypes.Command, cmd.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try { return await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new NatPunchException("NAT punch command timed out."); }
                }
            }
            finally { NatPunchRegistry.Remove(id); }
        }

        internal void OnEvent(NatPunchEvent evt)
        {
            TaskCompletionSource<NatPunchTarget>? waiting;
            lock (_gate)
            {
                if (_code == null || _code != evt.Code) return;    // not my session
                if (evt.IsHost == _isHost) return;                 // my own candidates echoed to a co-located client — not for me
                waiting = _waitingTarget;
            }
            var target = EndPointCodec.ToTarget(evt);
            waiting?.TrySetResult(target);
            TargetReceived?.Invoke(target);
        }
    }

    // ---- server-side ----

    internal sealed class NatPunchSession
    {
        public string Code = "";
        public BasePeer Host = null!;
        public string HostPublic = "";
        public string[] HostPrivate = Array.Empty<string>();
    }

    internal sealed class NatPunchServerState
    {
        private static readonly char[] Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();
        public readonly ConcurrentDictionary<string, NatPunchSession> Sessions = new ConcurrentDictionary<string, NatPunchSession>();
        // Which session each live host owns: peer id (Guid) -> code, for cleanup on disconnect/cancel.
        public readonly ConcurrentDictionary<Guid, string> Owned = new ConcurrentDictionary<Guid, string>();

        public NatPunchSession Allocate(BasePeer host)
        {
            while (true)
            {
                var code = GenerateCode();
                var session = new NatPunchSession { Code = code, Host = host };
                if (Sessions.TryAdd(code, session)) return session;
            }
        }

        private static string GenerateCode()
        {
            var bytes = new byte[6];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var chars = new char[6];
            for (var i = 0; i < 6; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
            return new string(chars);
        }
    }

    /// <summary>Server-side NAT punch coordinator. Enable with <see cref="NatPunchServerExtensions.UseNatPunch"/>.</summary>
    public static class NatPunchServer
    {
        private static readonly ConcurrentDictionary<BaseServer, NatPunchServerState> Servers
            = new ConcurrentDictionary<BaseServer, NatPunchServerState>();

        internal static void Enable(BaseServer server)
        {
            var state = Servers.GetOrAdd(server, _ => new NatPunchServerState());
            server.PeerDisconnected += peer => RemoveOwned(state, peer);
        }

        internal static async Task OnCommand(BasePeer peer, NatPunchCommand cmd)
        {
            var server = peer.CurrentPeerInfo.Server;
            if (server == null || !Servers.TryGetValue(server, out var state)) return;

            switch (cmd.Op)
            {
                case NatPunchOp.Register:
                {
                    if (cmd.UdpPort < 1 || cmd.UdpPort > ushort.MaxValue)
                    { await Reply(peer, cmd.CorrelationId, false, "Invalid UDP port.", "").ConfigureAwait(false); break; }

                    var publicEp = ObservedEndPoint(peer, cmd.UdpPort);
                    var session = state.Allocate(peer);
                    session.HostPublic = publicEp;
                    session.HostPrivate = cmd.PrivateEndPoints;
                    state.Owned[peer.CurrentPeerInfo.Id] = session.Code;
                    await Reply(peer, cmd.CorrelationId, true, "", session.Code).ConfigureAwait(false);
                    break;
                }
                case NatPunchOp.Punch:
                {
                    if (!state.Sessions.TryGetValue(cmd.Code ?? "", out var session))
                    { await Reply(peer, cmd.CorrelationId, false, "No such punch session.", "").ConfigureAwait(false); break; }
                    if (cmd.UdpPort < 1 || cmd.UdpPort > ushort.MaxValue)
                    { await Reply(peer, cmd.CorrelationId, false, "Invalid UDP port.", "").ConfigureAwait(false); break; }

                    var guestPublic = ObservedEndPoint(peer, cmd.UdpPort);
                    await Reply(peer, cmd.CorrelationId, true, "", session.Code).ConfigureAwait(false);

                    // Push both sides the counterpart's candidates at (nearly) the same time so they punch simultaneously.
                    var toHost = new NatPunchEvent { Code = session.Code, IsHost = false, PublicEndPoint = guestPublic, PrivateEndPoints = cmd.PrivateEndPoints };
                    var toGuest = new NatPunchEvent { Code = session.Code, IsHost = true, PublicEndPoint = session.HostPublic, PrivateEndPoints = session.HostPrivate };
                    await Send(session.Host, toHost).ConfigureAwait(false);
                    await Send(peer, toGuest).ConfigureAwait(false);
                    break;
                }
                case NatPunchOp.Cancel:
                    RemoveOwned(state, peer);
                    await Reply(peer, cmd.CorrelationId, true, "", "").ConfigureAwait(false);
                    break;
            }
        }

        /// <summary>
        /// The peer's server-observed source address combined with its self-reported UDP punch port. Empty when
        /// the transport exposes no remote endpoint (e.g. in-memory) — the counterpart then relies on the
        /// private candidates alone.
        /// </summary>
        private static string ObservedEndPoint(BasePeer peer, int udpPort)
        {
            var remote = peer.RemoteEndPoint;
            if (remote == null) return "";
            return EndPointCodec.Format(new IPEndPoint(remote.Address, udpPort));
        }

        private static void RemoveOwned(NatPunchServerState state, BasePeer peer)
        {
            if (state.Owned.TryRemove(peer.CurrentPeerInfo.Id, out var code))
                state.Sessions.TryRemove(code, out _);
        }

        private static Task Send(BasePeer peer, NatPunchEvent evt)
        {
            // Sent via SendAsync (serializer-wrapped) to match the auto-discovered IClientMessageHandler<byte[]> invoker.
            try { return peer.SendAsync(NatPunchTypes.Event, evt.Encode(), DeliveryMethod.Reliable); }
            catch { return Task.CompletedTask; }
        }

        private static Task Reply(BasePeer peer, int corr, bool ok, string err, string code)
        {
            var reply = new NatPunchReply { CorrelationId = corr, Success = ok, Error = err, Code = code };
            return peer.SendAsync(NatPunchTypes.Reply, reply.Encode(), DeliveryMethod.Reliable);
        }
    }

    // ---- the actual UDP hole puncher ----

    /// <summary>
    /// Performs the UDP hole punch once both sides have each other's candidates: fires probe datagrams at every
    /// candidate endpoint while listening for the counterpart's probes/acks on the same socket, and reports the
    /// first endpoint the two sides actually exchanged datagrams on. Both sides must run this at the same time
    /// (the coordinator's simultaneous events arrange exactly that).
    /// </summary>
    public static class NatPuncher
    {
        // 4-byte magics so stray datagrams on the port are ignored: "SNP" + kind.
        private static readonly byte[] Probe = { 0x53, 0x4E, 0x50, 0x01 };
        private static readonly byte[] Ack = { 0x53, 0x4E, 0x50, 0x02 };

        /// <summary>
        /// Attempts the punch from local UDP port <paramref name="localPort"/> against every candidate in
        /// <paramref name="target"/>. Returns the endpoint the hole was opened on, or null if nothing connected
        /// within <paramref name="timeoutMs"/>. After success, immediately reuse the same local port for the real
        /// traffic (e.g. a SetNet UDP client) — NAT mappings expire within seconds when idle.
        /// </summary>
        public static async Task<IPEndPoint?> TryPunchAsync(int localPort, NatPunchTarget target, int timeoutMs = 5000, CancellationToken cancellationToken = default)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));

            var candidates = new List<IPEndPoint>();
            if (target.PublicEndPoint != null) candidates.Add(target.PublicEndPoint);
            foreach (var ep in target.PrivateEndPoints) candidates.Add(ep);
            if (candidates.Count == 0) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));

            var established = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Receive loop: an incoming probe means their datagrams reach us — ack it (and keep acking so the
            // other side also completes); an incoming ack means the path works both ways.
            var receiver = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    UdpReceiveResult result;
                    try { result = await socket.ReceiveAsync().ConfigureAwait(false); }
                    catch { break; }   // socket disposed or cancelled

                    if (result.Buffer.Length != 4 || result.Buffer[0] != 0x53 || result.Buffer[1] != 0x4E || result.Buffer[2] != 0x50) continue;
                    if (result.Buffer[3] == Probe[3])
                    {
                        try { await socket.SendAsync(Ack, Ack.Length, result.RemoteEndPoint).ConfigureAwait(false); } catch { }
                        established.TrySetResult(result.RemoteEndPoint);
                    }
                    else if (result.Buffer[3] == Ack[3])
                    {
                        established.TrySetResult(result.RemoteEndPoint);
                    }
                }
            });

            // Probe loop: hammer every candidate until something answers or we time out. The first outbound
            // datagram is what opens our own NAT mapping toward each candidate.
            var prober = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested && !established.Task.IsCompleted)
                {
                    foreach (var ep in candidates)
                    {
                        try { await socket.SendAsync(Probe, Probe.Length, ep).ConfigureAwait(false); } catch { }
                    }
                    try { await Task.Delay(100, cts.Token).ConfigureAwait(false); } catch { break; }
                }
            });

            using (cts.Token.Register(() => established.TrySetCanceled()))
            {
                IPEndPoint? result;
                try { result = await established.Task.ConfigureAwait(false); }
                catch (OperationCanceledException) { result = null; }

                // Give the counterpart a short grace window to receive our ack before the socket goes away.
                if (result != null) { try { await Task.Delay(150).ConfigureAwait(false); } catch { } }

                cts.Cancel();
                socket.Close();
                try { await Task.WhenAll(receiver, prober).ConfigureAwait(false); } catch { }
                return result;
            }
        }

        /// <summary>
        /// Enumerates this machine's IPv4 unicast addresses (loopback excluded) as "ip:port" candidates for
        /// <paramref name="port"/> — the private endpoints peers behind the same NAT can reach directly.
        /// </summary>
        public static string[] GetPrivateEndPoints(int port)
        {
            var result = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        result.Add(EndPointCodec.Format(new IPEndPoint(addr.Address, port)));
                    }
                }
            }
            catch { /* interface enumeration can fail in sandboxes — private candidates are best-effort */ }
            return result.ToArray();
        }
    }

    // ---- auto-discovered handlers ----

    /// <summary>Auto-discovered server handler for NAT punch commands.</summary>
    [MessageHandler(NatPunchTypes.Command)]
    public sealed class NatPunchCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data) => NatPunchServer.OnCommand(peer, NatPunchCommand.Decode(data));
    }

    /// <summary>Auto-discovered client handler for correlated NAT punch replies.</summary>
    [MessageHandler(NatPunchTypes.Reply)]
    public sealed class NatPunchReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var r = NatPunchReply.Decode(data); NatPunchRegistry.Complete(r.CorrelationId, r); return Task.CompletedTask; }
    }

    /// <summary>Auto-discovered client handler for NAT punch target events.</summary>
    [MessageHandler(NatPunchTypes.Event)]
    public sealed class NatPunchEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { NatPunchRegistry.DispatchEvent(NatPunchEvent.Decode(data)); return Task.CompletedTask; }
    }

    // ---- composition entry points ----

    /// <summary>Attaches the NAT punch coordinator to a server by composition.</summary>
    public static class NatPunchServerExtensions
    {
        /// <summary>Enables the server-side punch coordinator (session registry + endpoint exchange).</summary>
        public static void UseNatPunch(this BaseServer server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            NatPunchServer.Enable(server);
        }
    }

    /// <summary>Attaches a NAT punch driver to a client by composition.</summary>
    public static class NatPunchClientExtensions
    {
        /// <summary>Enables client-side NAT punching; returns the driver (register/punch/cancel + target events).</summary>
        public static NatPunchClient UseNatPunch(this BaseClient client) => new NatPunchClient(client);
    }

    /// <summary>One-time bootstrap so the NAT punch handlers are discovered. Call at startup.</summary>
    public static class NatPunchRuntime
    {
        /// <summary>Ensures the NAT punch layer is discoverable.</summary>
        public static void Enable() { _ = NatPunchTypes.Command; }
    }
}
