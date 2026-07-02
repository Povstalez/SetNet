using SetNet.Config;
using SetNet.Core;
using SetNet.StateSync;

namespace StateSync.Server;

/// <summary>Minimal peer — StateSync replicates to it automatically once it connects (AutoObserve is on by default).</summary>
public sealed class BallPeer : BasePeer
{
    public BallPeer(PeerInfo peerInfo) : base(peerInfo) { }

    // Fires once for every disconnect — graceful (client called Disconnect), a kick, or after a failure.
    protected override void OnDisconnected() => Console.WriteLine($"[server] peer disconnected: {CurrentPeerInfo.Id}");

    // Fires only on an UNEXPECTED drop (network error / crash / heartbeat timeout), before OnDisconnected.
    protected override void OnUnexpectedDisconnect() => Console.WriteLine($"[server] peer lost unexpectedly: {CurrentPeerInfo.Id}");

    protected override void OnError(string error) => Console.WriteLine($"[server] peer error: {error}");
}

/// <summary>Minimal server; the world is driven in Program.cs via the ServerReplication returned by UseStateSync.</summary>
public sealed class BallServer : BaseServer
{
    public BallServer(Configuration config) : base(config) { }
    protected override BasePeer OnNewClient(PeerInfo peerInfo) => new BallPeer(peerInfo);
}

/// <summary>Server-side simulation state for one ball: it bounces inside a ±10 cube. The entity's fields are set from this each frame.</summary>
public sealed class Ball
{
    private const float Bound = 10f;

    public NetworkEntity Entity { get; }
    private Vec3 _pos;
    private Vec3 _vel;
    private readonly float _hue;

    public Ball(NetworkEntity entity, Random rng)
    {
        Entity = entity;
        _pos = new Vec3(Rand(rng), Rand(rng), Rand(rng));
        _vel = new Vec3(Rand(rng) * 0.4f, Rand(rng) * 0.4f, Rand(rng) * 0.4f);
        _hue = (float)rng.NextDouble();
        Push();
    }

    /// <summary>Advances the ball by <paramref name="dt"/> seconds, bouncing off the walls, and writes the new state to the entity.</summary>
    public void Step(float dt)
    {
        float x = _pos.X + _vel.X * dt * 10f, y = _pos.Y + _vel.Y * dt * 10f, z = _pos.Z + _vel.Z * dt * 10f;
        float vx = _vel.X, vy = _vel.Y, vz = _vel.Z;
        if (x < -Bound || x > Bound) { vx = -vx; x = Math.Clamp(x, -Bound, Bound); }
        if (y < -Bound || y > Bound) { vy = -vy; y = Math.Clamp(y, -Bound, Bound); }
        if (z < -Bound || z > Bound) { vz = -vz; z = Math.Clamp(z, -Bound, Bound); }
        _pos = new Vec3(x, y, z);
        _vel = new Vec3(vx, vy, vz);
        Push();
    }

    private void Push()
    {
        Entity.SetVec3(Shared.World.Position, _pos);
        Entity.SetFloat(Shared.World.Hue, _hue);
    }

    private static float Rand(Random rng) => (float)(rng.NextDouble() * 2 - 1);   // -1..1
}
