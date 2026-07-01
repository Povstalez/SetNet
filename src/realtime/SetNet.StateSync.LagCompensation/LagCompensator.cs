using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SetNet.StateSync.LagCompensation
{
    /// <summary>
    /// Server-side lag compensation: records a short history of every entity's position each tick, then lets you **rewind**
    /// the world to where things were at a past moment. When a client fires, it sees the world delayed by its interpolation
    /// buffer + network latency; to judge the hit fairly, rewind to <c>now − (interpolationDelay + RTT/2)</c> and test
    /// against those historical positions. You supply how to read an entity's position and how long to keep history.
    /// </summary>
    public sealed class LagCompensator
    {
        private readonly Func<NetworkEntity, Vec3> _position;
        private readonly double _historySeconds;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new object();
        private readonly LinkedList<Frame> _frames = new LinkedList<Frame>();   // oldest → newest

        private struct Frame { public double Time; public Dictionary<uint, Vec3> Positions; }

        /// <summary>Creates a lag compensator keeping <paramref name="historyMs"/> of position history.</summary>
        public LagCompensator(Func<NetworkEntity, Vec3> positionSelector, int historyMs = 1000)
        {
            _position = positionSelector ?? throw new ArgumentNullException(nameof(positionSelector));
            _historySeconds = Math.Max(0.05, historyMs / 1000.0);
        }

        /// <summary>The compensator's monotonic clock, in seconds (use it to compute rewind offsets consistently).</summary>
        public double NowSeconds => _clock.Elapsed.TotalSeconds;

        /// <summary>Records the current positions of all entities. Call once per server tick (e.g. from your simulation loop).</summary>
        public void Capture(IEnumerable<NetworkEntity> entities)
        {
            var now = _clock.Elapsed.TotalSeconds;
            var positions = new Dictionary<uint, Vec3>();
            foreach (var e in entities) positions[e.NetId] = _position(e);

            lock (_gate)
            {
                _frames.AddLast(new Frame { Time = now, Positions = positions });
                while (_frames.Count > 1 && now - _frames.First!.Value.Time > _historySeconds)
                    _frames.RemoveFirst();
            }
        }

        /// <summary>The interpolated position of an entity <paramref name="secondsAgo"/> in the past, or null if unknown/too old.</summary>
        public Vec3? PositionAgo(uint netId, double secondsAgo)
            => PositionAt(netId, _clock.Elapsed.TotalSeconds - Math.Max(0, secondsAgo));

        /// <summary>The interpolated position of an entity at an absolute compensator time, or null if unavailable.</summary>
        public Vec3? PositionAt(uint netId, double time)
        {
            lock (_gate)
            {
                if (_frames.Count == 0) return null;

                Frame? before = null, after = null;
                for (var node = _frames.First; node != null; node = node.Next)
                {
                    var f = node.Value;
                    if (!f.Positions.ContainsKey(netId)) continue;
                    if (f.Time <= time) before = f;
                    if (f.Time >= time && after == null) { after = f; break; }
                }

                if (before == null && after == null) return null;
                if (before == null) return after!.Value.Positions[netId];
                if (after == null || after.Value.Time <= before.Value.Time) return before.Value.Positions[netId];

                var a = before.Value; var b = after.Value;
                var t = (float)((time - a.Time) / (b.Time - a.Time));
                if (t < 0) t = 0; else if (t > 1) t = 1;
                return Vec3.Lerp(a.Positions[netId], b.Positions[netId], t);
            }
        }
    }
}
