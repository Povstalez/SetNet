using System;
using System.IO;

namespace SetNet.GeoData
{
    /// <summary>
    /// Serializes/deserializes baked geometry to a compact, engine-independent binary blob. A tool (e.g. the Unity
    /// baker) writes it once; a headless server loads it at boot via <see cref="Load(System.IO.Stream)"/> — no engine
    /// dependency at runtime. Handles both grid and nav-mesh geometry behind the same file.
    /// </summary>
    public static class GeoDataFile
    {
        private static readonly byte[] Magic = { (byte)'S', (byte)'N', (byte)'G', (byte)'D' };
        private const byte Version = 1;
        private const byte KindGrid = 1;
        private const byte KindNavMesh = 2;
        private const byte KindLayeredGrid = 3;

        /// <summary>Writes geometry to a stream.</summary>
        public static void Save(IGeoData geo, Stream stream)
        {
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write(Magic);
            w.Write(Version);
            switch (geo)
            {
                case GridGeoData g: w.Write(KindGrid); WriteGrid(w, g); break;
                case LayeredGridGeoData lg: w.Write(KindLayeredGrid); WriteLayered(w, lg); break;
                case NavMeshGeoData m: w.Write(KindNavMesh); WriteNavMesh(w, m); break;
                default: throw new NotSupportedException($"Cannot serialize {geo.GetType().Name}.");
            }
        }

        /// <summary>Reads geometry from a stream.</summary>
        public static IGeoData Load(Stream stream)
        {
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
                throw new InvalidDataException("Not a GeoData file.");
            var version = r.ReadByte();
            if (version != Version) throw new InvalidDataException($"Unsupported GeoData version {version}.");
            var kind = r.ReadByte();
            return kind switch
            {
                KindGrid => ReadGrid(r),
                KindLayeredGrid => ReadLayered(r),
                KindNavMesh => ReadNavMesh(r),
                _ => throw new InvalidDataException($"Unknown GeoData kind {kind}."),
            };
        }

        /// <summary>Writes geometry to a file path.</summary>
        public static void SaveToFile(IGeoData geo, string path)
        {
            using var fs = File.Create(path);
            Save(geo, fs);
        }

        /// <summary>Reads geometry from a file path.</summary>
        public static IGeoData LoadFromFile(string path)
        {
            using var fs = File.OpenRead(path);
            return Load(fs);
        }

        private static void WriteGrid(BinaryWriter w, GridGeoData g)
        {
            w.Write(g.Origin.X); w.Write(g.Origin.Y); w.Write(g.Origin.Z);
            w.Write(g.CellSize); w.Write(g.Width); w.Write(g.Depth); w.Write(g.MaxStep);
            var flags = g.Flags; var heights = g.Heights;
            w.Write(flags.Length);
            for (var i = 0; i < flags.Length; i++) { w.Write(flags[i]); w.Write(heights[i]); }
        }

        private static IGeoData ReadGrid(BinaryReader r)
        {
            var origin = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var cell = r.ReadSingle(); var w = r.ReadInt32(); var d = r.ReadInt32(); var maxStep = r.ReadSingle();
            var n = r.ReadInt32();
            var b = new GridGeoDataBuilder(origin, cell, w, d).SetMaxStep(maxStep);
            for (var cz = 0; cz < d; cz++)
                for (var cx = 0; cx < w; cx++)
                {
                    var flag = r.ReadByte(); var h = r.ReadSingle();
                    if ((flag & GridGeoData.FlagBlocked) != 0) b.SetBlocked(cx, cz);
                    else if ((flag & GridGeoData.FlagWalkable) != 0) b.SetWalkable(cx, cz, h);
                }
            _ = n;
            return b.Build();
        }

        private static void WriteLayered(BinaryWriter w, LayeredGridGeoData g)
        {
            w.Write(g.Origin.X); w.Write(g.Origin.Y); w.Write(g.Origin.Z);
            w.Write(g.CellSize); w.Write(g.Width); w.Write(g.Depth);
            w.Write(g.MaxStep); w.Write(g.LayerMatchTolerance);
            var start = g.CellStart; var ys = g.LayerYs; var walks = g.LayerWalks; var walls = g.Walls;
            var n = g.Width * g.Depth;
            for (var i = 0; i < n; i++)
            {
                w.Write(walls[i]);
                var count = start[i + 1] - start[i];
                w.Write(count);
                for (var l = start[i]; l < start[i + 1]; l++) { w.Write(ys[l]); w.Write(walks[l]); }
            }
        }

        private static IGeoData ReadLayered(BinaryReader r)
        {
            var origin = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var cell = r.ReadSingle(); var w = r.ReadInt32(); var d = r.ReadInt32();
            var maxStep = r.ReadSingle(); var matchTol = r.ReadSingle();
            var b = new LayeredGridGeoDataBuilder(origin, cell, w, d).SetMaxStep(maxStep).SetLayerMatchTolerance(matchTol);
            for (var cz = 0; cz < d; cz++)
                for (var cx = 0; cx < w; cx++)
                {
                    var wall = r.ReadBoolean();
                    var count = r.ReadInt32();
                    for (var l = 0; l < count; l++) { var y = r.ReadSingle(); var walk = r.ReadByte() != 0; b.AddLayer(cx, cz, y, walk); }
                    if (wall) b.SetWall(cx, cz);
                }
            return b.Build();
        }

        private static void WriteNavMesh(BinaryWriter w, NavMeshGeoData m)
        {
            var verts = m.Vertices; var tris = m.Indices;
            w.Write(verts.Length);
            foreach (var v in verts) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            w.Write(tris.Length);
            foreach (var i in tris) w.Write(i);
        }

        private static IGeoData ReadNavMesh(BinaryReader r)
        {
            var vc = r.ReadInt32();
            var verts = new Vec3[vc];
            for (var i = 0; i < vc; i++) verts[i] = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var ic = r.ReadInt32();
            var tris = new int[ic];
            for (var i = 0; i < ic; i++) tris[i] = r.ReadInt32();
            return NavMeshGeoData.FromTriangles(verts, tris);
        }
    }
}
