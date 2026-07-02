using System;
using System.Collections.Generic;

namespace SetNet.GeoData
{
    /// <summary>
    /// A navigation mesh: a set of walkable triangles (typically exported from an engine's baked NavMesh). More
    /// precise than a grid for irregular 3D worlds. Exposes triangle adjacency + shared-edge portals so
    /// <c>SetNet.PathFinding</c> can A* over polygons and funnel a smooth path. Build with
    /// <see cref="FromTriangles"/> or load a baked <see cref="GeoDataFile"/>.
    /// </summary>
    public sealed class NavMeshGeoData : IGeoData
    {
        private readonly Vec3[] _verts;
        private readonly int[] _tris;             // 3 vertex indices per triangle
        private readonly int[] _adj;              // 3 neighbour triangle indices per triangle (-1 = border edge)
        private readonly Bounds _bounds;

        // Uniform XZ grid acceleration: cell -> triangle indices overlapping it.
        private readonly float _accelCell;
        private readonly int _accelW, _accelD;
        private readonly Vec3 _accelOrigin;
        private readonly List<int>[] _accel;

        /// <summary>Distance between sample points when testing can-walk-straight along a segment (world units). Default 0.5.</summary>
        public float WalkSampleStep { get; set; } = 0.5f;

        /// <summary>
        /// Vertical tolerance (world units) for staying "on a floor" during multi-storey queries: how far a sampled
        /// point may be above/below a surface before it counts as off that floor. Keeps overlapping floors (a bridge
        /// over a road, a building's storeys) separate — walking straight can't jump between floors through the air.
        /// Default 0.75.
        /// </summary>
        public float WalkYTolerance { get; set; } = 0.75f;

        private NavMeshGeoData(Vec3[] verts, int[] tris)
        {
            _verts = verts; _tris = tris;
            _adj = BuildAdjacency(tris);
            _bounds = ComputeBounds(verts);

            // Build the acceleration grid (~ a handful of triangles per cell on average).
            var size = _bounds.Size;
            var triCount = Math.Max(1, tris.Length / 3);
            var area = Math.Max(1e-3f, size.X * size.Z);
            _accelCell = Math.Max(0.5f, (float)Math.Sqrt(area / triCount));
            _accelOrigin = _bounds.Min;
            _accelW = Math.Max(1, (int)Math.Ceiling(size.X / _accelCell));
            _accelD = Math.Max(1, (int)Math.Ceiling(size.Z / _accelCell));
            _accel = new List<int>[_accelW * _accelD];
            for (var t = 0; t < triCount; t++)
            {
                Tri(t, out var a, out var b, out var c);
                int minx = AccelX(Math.Min(a.X, Math.Min(b.X, c.X))), maxx = AccelX(Math.Max(a.X, Math.Max(b.X, c.X)));
                int minz = AccelZ(Math.Min(a.Z, Math.Min(b.Z, c.Z))), maxz = AccelZ(Math.Max(a.Z, Math.Max(b.Z, c.Z)));
                for (var gz = minz; gz <= maxz; gz++)
                    for (var gx = minx; gx <= maxx; gx++)
                    {
                        var idx = gz * _accelW + gx;
                        (_accel[idx] ??= new List<int>()).Add(t);
                    }
            }
        }

        /// <summary>Builds a nav-mesh from a triangle soup: <paramref name="vertices"/> and <paramref name="triangleIndices"/> (3 per triangle, CCW when viewed from above).</summary>
        public static NavMeshGeoData FromTriangles(Vec3[] vertices, int[] triangleIndices)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (triangleIndices == null || triangleIndices.Length % 3 != 0) throw new ArgumentException("triangleIndices must be a multiple of 3.", nameof(triangleIndices));
            return new NavMeshGeoData(vertices, triangleIndices);
        }

        /// <inheritdoc/>
        public Bounds Bounds => _bounds;

        /// <summary>Number of triangles.</summary>
        public int TriangleCount => _tris.Length / 3;

        /// <summary>Gets a triangle's three world-space corners.</summary>
        public void GetTriangle(int t, out Vec3 a, out Vec3 b, out Vec3 c) => Tri(t, out a, out b, out c);

        /// <summary>The centroid of a triangle.</summary>
        public Vec3 TriangleCentroid(int t) { Tri(t, out var a, out var b, out var c); return (a + b + c) * (1f / 3f); }

        /// <summary>
        /// The walkable triangle under the point, or -1 if the point is off the mesh. **Multi-storey aware:** when
        /// several triangles overlap this XZ (stacked floors), it returns the one whose surface height is nearest the
        /// point's Y — so a point on the second floor resolves to the second-floor triangle, not the ground below it.
        /// </summary>
        public int TriangleAt(Vec3 p)
        {
            var gx = AccelX(p.X); var gz = AccelZ(p.Z);
            var list = (gx >= 0 && gx < _accelW && gz >= 0 && gz < _accelD) ? _accel[gz * _accelW + gx] : null;
            if (list == null) return -1;
            int best = -1; float bestDy = float.PositiveInfinity;
            foreach (var t in list)
            {
                Tri(t, out var a, out var b, out var c);
                if (!InTriangleXZ(p, a, b, c)) continue;
                Barycentric(p, a, b, c, out var u, out var v, out var w);
                var dy = Math.Abs((u * a.Y + v * b.Y + w * c.Y) - p.Y);
                if (dy < bestDy) { bestDy = dy; best = t; }
            }
            return best;
        }

        /// <summary>The neighbour triangle across edge <paramref name="edge"/> (0..2) of triangle <paramref name="t"/>, or -1 for a border edge.</summary>
        public int Neighbour(int t, int edge) => _adj[t * 3 + edge];

        /// <summary>The two shared-edge vertices between adjacent triangles <paramref name="t"/> and <paramref name="neighbour"/> (false if not adjacent).</summary>
        public bool SharedEdge(int t, int neighbour, out Vec3 v0, out Vec3 v1)
        {
            for (var e = 0; e < 3; e++)
                if (_adj[t * 3 + e] == neighbour)
                {
                    v0 = _verts[_tris[t * 3 + e]];
                    v1 = _verts[_tris[t * 3 + (e + 1) % 3]];
                    return true;
                }
            v0 = v1 = Vec3.Zero;
            return false;
        }

        /// <inheritdoc/>
        public bool IsWalkable(Vec3 point) => TriangleAt(point) >= 0;

        /// <inheritdoc/>
        public float SampleHeight(Vec3 point)
        {
            var t = TriangleAt(point);
            if (t < 0) return float.NaN;
            Tri(t, out var a, out var b, out var c);
            Barycentric(point, a, b, c, out var u, out var v, out var w);
            return u * a.Y + v * b.Y + w * c.Y;
        }

        /// <inheritdoc/>
        public Vec3 SampleNearestWalkable(Vec3 point)
        {
            var t = TriangleAt(point);
            if (t >= 0) { Tri(t, out var a, out var b, out var c); Barycentric(point, a, b, c, out var u, out var v, out var w); return new Vec3(point.X, u * a.Y + v * b.Y + w * c.Y, point.Z); }
            // Nearest point on the nearest triangle (brute force — nearest-snapping is infrequent).
            var best = point; var bestD = float.PositiveInfinity;
            for (var i = 0; i < TriangleCount; i++)
            {
                Tri(i, out var a, out var b, out var c);
                var cp = ClosestPointOnTriangle(point, a, b, c);
                var d = Vec3.DistanceSquared(point, cp);
                if (d < bestD) { bestD = d; best = cp; }
            }
            return best;
        }

        /// <summary>On a nav-mesh, "line of sight" is treated as an unobstructed walkable path — the mesh models floor, not walls. Equivalent to <see cref="CanWalkStraight"/>.</summary>
        public bool LineOfSight(Vec3 from, Vec3 to) => CanWalkStraight(from, to);

        /// <inheritdoc/>
        public bool CanWalkStraight(Vec3 from, Vec3 to)
        {
            var dist = Vec3.HorizontalDistance(from, to);
            var steps = Math.Max(1, (int)Math.Ceiling(dist / Math.Max(0.05f, WalkSampleStep)));
            for (var i = 0; i <= steps; i++)
            {
                var s = Vec3.Lerp(from, to, (float)i / steps);
                var t = TriangleAt(s);
                if (t < 0) return false;                                   // off the mesh
                Tri(t, out var a, out var b, out var c);
                Barycentric(s, a, b, c, out var u, out var v, out var w);
                var surfaceY = u * a.Y + v * b.Y + w * c.Y;
                if (Math.Abs(surfaceY - s.Y) > WalkYTolerance) return false; // sample left the floor (through air / another storey)
            }
            return true;
        }

        /// <inheritdoc/>
        public RaycastHit Raycast(Vec3 origin, Vec3 direction, float maxDistance)
        {
            var dir = direction.Normalized;
            if (dir.LengthSquared < 1e-9f) return RaycastHit.None;
            var best = RaycastHit.None; var bestT = maxDistance;
            for (var i = 0; i < TriangleCount; i++)
            {
                Tri(i, out var a, out var b, out var c);
                if (RayTriangle(origin, dir, a, b, c, out var tHit) && tHit >= 0 && tHit <= bestT)
                {
                    var n = Vec3.Cross(b - a, c - a).Normalized;
                    best = new RaycastHit(true, origin + dir * tHit, tHit, n);
                    bestT = tHit;
                }
            }
            return best;
        }

        // --- internal accessors for serialization ---
        internal Vec3[] Vertices => _verts;
        internal int[] Indices => _tris;

        // --- helpers ---
        private void Tri(int t, out Vec3 a, out Vec3 b, out Vec3 c)
        { a = _verts[_tris[t * 3]]; b = _verts[_tris[t * 3 + 1]]; c = _verts[_tris[t * 3 + 2]]; }

        private int AccelX(float x) { var i = (int)Math.Floor((x - _accelOrigin.X) / _accelCell); return i < 0 ? 0 : (i >= _accelW ? _accelW - 1 : i); }
        private int AccelZ(float z) { var i = (int)Math.Floor((z - _accelOrigin.Z) / _accelCell); return i < 0 ? 0 : (i >= _accelD ? _accelD - 1 : i); }

        private static Bounds ComputeBounds(Vec3[] v)
        {
            if (v.Length == 0) return new Bounds(Vec3.Zero, Vec3.Zero);
            float minx = v[0].X, miny = v[0].Y, minz = v[0].Z, maxx = v[0].X, maxy = v[0].Y, maxz = v[0].Z;
            foreach (var p in v)
            {
                if (p.X < minx) minx = p.X; if (p.Y < miny) miny = p.Y; if (p.Z < minz) minz = p.Z;
                if (p.X > maxx) maxx = p.X; if (p.Y > maxy) maxy = p.Y; if (p.Z > maxz) maxz = p.Z;
            }
            return new Bounds(new Vec3(minx, miny, minz), new Vec3(maxx, maxy, maxz));
        }

        private static int[] BuildAdjacency(int[] tris)
        {
            var triCount = tris.Length / 3;
            var adj = new int[triCount * 3];
            for (var i = 0; i < adj.Length; i++) adj[i] = -1;
            // Map an undirected edge (min,max) -> (triangle, edge) of the first triangle seen.
            var edges = new Dictionary<long, (int tri, int edge)>();
            for (var t = 0; t < triCount; t++)
                for (var e = 0; e < 3; e++)
                {
                    int va = tris[t * 3 + e], vb = tris[t * 3 + (e + 1) % 3];
                    long key = ((long)Math.Min(va, vb) << 32) | (uint)Math.Max(va, vb);
                    if (edges.TryGetValue(key, out var other))
                    {
                        adj[t * 3 + e] = other.tri;
                        adj[other.tri * 3 + other.edge] = t;
                    }
                    else edges[key] = (t, e);
                }
            return adj;
        }

        private static bool InTriangleXZ(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
        {
            Barycentric(p, a, b, c, out var u, out var v, out var w);
            const float eps = -1e-4f;
            return u >= eps && v >= eps && w >= eps;
        }

        private static void Barycentric(Vec3 p, Vec3 a, Vec3 b, Vec3 c, out float u, out float v, out float w)
        {
            // XZ-plane barycentric coordinates.
            float v0x = b.X - a.X, v0z = b.Z - a.Z;
            float v1x = c.X - a.X, v1z = c.Z - a.Z;
            float v2x = p.X - a.X, v2z = p.Z - a.Z;
            var den = v0x * v1z - v1x * v0z;
            if (Math.Abs(den) < 1e-12f) { u = 1; v = 0; w = 0; return; }
            var inv = 1f / den;
            v = (v2x * v1z - v1x * v2z) * inv;
            w = (v0x * v2z - v2x * v0z) * inv;
            u = 1f - v - w;
        }

        private static bool RayTriangle(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c, out float t)
        {
            t = 0;
            var e1 = b - a; var e2 = c - a;
            var pv = Vec3.Cross(d, e2);
            var det = Vec3.Dot(e1, pv);
            if (Math.Abs(det) < 1e-8f) return false;
            var inv = 1f / det;
            var tv = o - a;
            var u = Vec3.Dot(tv, pv) * inv;
            if (u < 0 || u > 1) return false;
            var qv = Vec3.Cross(tv, e1);
            var v = Vec3.Dot(d, qv) * inv;
            if (v < 0 || u + v > 1) return false;
            t = Vec3.Dot(e2, qv) * inv;
            return t >= 0;
        }

        private static Vec3 ClosestPointOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
        {
            // Ericson, Real-Time Collision Detection.
            var ab = b - a; var ac = c - a; var ap = p - a;
            float d1 = Vec3.Dot(ab, ap), d2 = Vec3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;
            var bp = p - b;
            float d3 = Vec3.Dot(ab, bp), d4 = Vec3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;
            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            var cp = p - c;
            float d5 = Vec3.Dot(ab, cp), d6 = Vec3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;
            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            var va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            var denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }
    }
}
