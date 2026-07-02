using System;
using System.IO;
using UnityEngine;

namespace SetNet.GeoData.Unity
{
    /// <summary>The geometry kind stored in a <c>.geo</c> file.</summary>
    public enum GeoKind
    {
        /// <summary>2.5D single-surface grid (file kind 1).</summary>
        Grid = 1,
        /// <summary>Nav-mesh (file kind 2).</summary>
        NavMesh = 2,
        /// <summary>Multi-storey layered grid (file kind 3).</summary>
        LayeredGrid = 3,
    }

    /// <summary>Parsed contents of a SetNet <c>.geo</c> file, in Unity types — used by the debug visualizer.</summary>
    public sealed class ParsedGeoData
    {
        /// <summary>Which representation this file holds.</summary>
        public GeoKind Kind;

        // Grid + layered grid common:
        /// <summary>Grid min corner (XZ anchor).</summary>
        public Vector3 Origin;
        /// <summary>Cell edge length.</summary>
        public float CellSize;
        /// <summary>Cells along X.</summary>
        public int Width;
        /// <summary>Cells along Z.</summary>
        public int Depth;
        /// <summary>Max climbable step.</summary>
        public float MaxStep;

        // Grid (kind 1):
        /// <summary>Per-cell flag bytes (bit0 walkable, bit1 blocked), row-major.</summary>
        public byte[] Flags;
        /// <summary>Per-cell ground height, row-major.</summary>
        public float[] Heights;

        // Layered grid (kind 3):
        /// <summary>Layer-match tolerance.</summary>
        public float LayerMatchTolerance;
        /// <summary>CSR offsets, length Width*Depth+1.</summary>
        public int[] CellStart;
        /// <summary>Flat per-layer heights.</summary>
        public float[] LayerHeights;
        /// <summary>Flat per-layer walkable flags.</summary>
        public byte[] LayerWalkable;
        /// <summary>Per-cell full-height wall flags, row-major.</summary>
        public bool[] Walls;

        // Nav-mesh (kind 2):
        /// <summary>Vertices.</summary>
        public Vector3[] Vertices;
        /// <summary>Triangle indices (3 per triangle).</summary>
        public int[] Indices;
    }

    /// <summary>
    /// Reads the portable SetNet GeoData binary format (magic "SNGD") into Unity types, purely for <b>debug
    /// visualization</b> in the editor. It mirrors the server's <c>GeoDataFile</c> reader but needs no reference to the
    /// SetNet assemblies (it only parses the bytes).
    /// </summary>
    public static class GeoDataFileReader
    {
        private const byte FlagWalkable = 1;
        private const byte FlagBlocked = 2;

        /// <summary>Bit 0: cell is walkable.</summary>
        public static bool IsWalkable(byte flag) => (flag & FlagWalkable) != 0 && (flag & FlagBlocked) == 0;
        /// <summary>Bit 1: cell is blocked.</summary>
        public static bool IsBlocked(byte flag) => (flag & FlagBlocked) != 0;

        /// <summary>Parses a <c>.geo</c> file from raw bytes.</summary>
        public static ParsedGeoData Read(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using var ms = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);

            var magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != 'S' || magic[1] != 'N' || magic[2] != 'G' || magic[3] != 'D')
                throw new InvalidDataException("Not a GeoData file.");
            var version = r.ReadByte();
            if (version != 1) throw new InvalidDataException($"Unsupported GeoData version {version}.");
            var kind = r.ReadByte();

            switch (kind)
            {
                case (byte)GeoKind.Grid: return ReadGrid(r);
                case (byte)GeoKind.LayeredGrid: return ReadLayered(r);
                case (byte)GeoKind.NavMesh: return ReadNavMesh(r);
                default: throw new InvalidDataException($"Unknown GeoData kind {kind}.");
            }
        }

        /// <summary>Parses a <c>.geo</c> file from a path.</summary>
        public static ParsedGeoData ReadFile(string path) => Read(File.ReadAllBytes(path));

        private static ParsedGeoData ReadGrid(BinaryReader r)
        {
            var p = new ParsedGeoData { Kind = GeoKind.Grid };
            p.Origin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            p.CellSize = r.ReadSingle();
            p.Width = r.ReadInt32();
            p.Depth = r.ReadInt32();
            p.MaxStep = r.ReadSingle();
            var n = r.ReadInt32();
            p.Flags = new byte[n];
            p.Heights = new float[n];
            for (var i = 0; i < n; i++) { p.Flags[i] = r.ReadByte(); p.Heights[i] = r.ReadSingle(); }
            return p;
        }

        private static ParsedGeoData ReadLayered(BinaryReader r)
        {
            var p = new ParsedGeoData { Kind = GeoKind.LayeredGrid };
            p.Origin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            p.CellSize = r.ReadSingle();
            p.Width = r.ReadInt32();
            p.Depth = r.ReadInt32();
            p.MaxStep = r.ReadSingle();
            p.LayerMatchTolerance = r.ReadSingle();

            var count = p.Width * p.Depth;
            p.Walls = new bool[count];
            p.CellStart = new int[count + 1];
            var heights = new System.Collections.Generic.List<float>();
            var walks = new System.Collections.Generic.List<byte>();
            var k = 0;
            for (var i = 0; i < count; i++)
            {
                p.CellStart[i] = k;
                p.Walls[i] = r.ReadBoolean();
                var layerCount = r.ReadInt32();
                for (var l = 0; l < layerCount; l++)
                {
                    heights.Add(r.ReadSingle());
                    walks.Add(r.ReadByte());
                    k++;
                }
            }
            p.CellStart[count] = k;
            p.LayerHeights = heights.ToArray();
            p.LayerWalkable = walks.ToArray();
            return p;
        }

        private static ParsedGeoData ReadNavMesh(BinaryReader r)
        {
            var p = new ParsedGeoData { Kind = GeoKind.NavMesh };
            var vc = r.ReadInt32();
            p.Vertices = new Vector3[vc];
            for (var i = 0; i < vc; i++) p.Vertices[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var ic = r.ReadInt32();
            p.Indices = new int[ic];
            for (var i = 0; i < ic; i++) p.Indices[i] = r.ReadInt32();
            return p;
        }
    }
}
