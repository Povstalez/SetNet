using System;
using System.IO;
using UnityEngine;

namespace SetNet.GeoData.Unity.Editor
{
    /// <summary>
    /// Writes the portable SetNet GeoData binary format (magic "SNGD", version 1) that a headless SetNet server
    /// loads via <c>GeoDataFile.LoadFromFile(...)</c>. This tool only WRITES the file, so it needs no reference to
    /// the <c>SetNet.GeoData</c> assembly — the byte layout below mirrors <c>GeoDataFile.WriteGrid</c>/
    /// <c>WriteNavMesh</c> field-for-field.
    /// <para>
    /// <b>Coordinate system:</b> Unity is Y-up, left-handed; the server's <c>Vec3</c> is also Y-up with X/Y/Z laid
    /// out identically, so vertices and origins are written component-wise as-is (no axis flip / handedness swap).
    /// </para>
    /// <para>
    /// <b>Endianness:</b> <see cref="BinaryWriter"/> emits little-endian on every platform, matching the
    /// <c>BinaryReader</c> the server reads with.
    /// </para>
    /// </summary>
    public static class GeoDataFileWriter
    {
        // Header constants — must exactly match SetNet.GeoData.GeoDataFile.
        private static readonly byte[] Magic = { (byte)'S', (byte)'N', (byte)'G', (byte)'D' };
        private const byte Version = 1;
        private const byte KindGrid = 1;
        private const byte KindNavMesh = 2;
        private const byte KindLayeredGrid = 3;

        // Cell flag bits — must match GridGeoData.FlagWalkable / FlagBlocked.
        /// <summary>Cell flag bit 0: the cell is walkable (an agent may stand on it).</summary>
        public const byte FlagWalkable = 1;
        /// <summary>Cell flag bit 1: the cell is blocked (a wall/obstacle occupies it).</summary>
        public const byte FlagBlocked = 2;

        /// <summary>
        /// Writes a 2.5D navigation grid (file kind 1). Cells are written row-major — <paramref name="depth"/>
        /// (cz) outer, <paramref name="width"/> (cx) inner — which is the exact order the server reads them back.
        /// </summary>
        /// <param name="stream">Destination stream (left open; caller owns it).</param>
        /// <param name="origin">Grid min corner. X/Z anchor the XZ plane; Y is informational (per-cell heights carry real ground Y).</param>
        /// <param name="cellSize">Cell edge length in world units.</param>
        /// <param name="width">Number of cells along X.</param>
        /// <param name="depth">Number of cells along Z.</param>
        /// <param name="maxStep">Largest ground-height step an agent may traverse between adjacent cells.</param>
        /// <param name="flags">Per-cell flag bytes (<see cref="FlagWalkable"/>/<see cref="FlagBlocked"/>), length = width*depth, row-major (cz*width + cx).</param>
        /// <param name="heights">Per-cell ground height, same length/order as <paramref name="flags"/>.</param>
        public static void WriteGrid(
            Stream stream,
            Vector3 origin,
            float cellSize,
            int width,
            int depth,
            float maxStep,
            byte[] flags,
            float[] heights)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (flags == null) throw new ArgumentNullException(nameof(flags));
            if (heights == null) throw new ArgumentNullException(nameof(heights));
            if (width < 0 || depth < 0) throw new ArgumentOutOfRangeException(nameof(width), "Grid dimensions must be non-negative.");

            var count = width * depth;
            if (flags.Length != count)
                throw new ArgumentException($"flags length {flags.Length} must equal width*depth ({count}).", nameof(flags));
            if (heights.Length != count)
                throw new ArgumentException($"heights length {heights.Length} must equal width*depth ({count}).", nameof(heights));

            // leaveOpen: true so callers using a FileStream/using block control disposal.
            using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            // Header: magic(4) + version(1) + kind(1).
            w.Write(Magic);
            w.Write(Version);
            w.Write(KindGrid);

            // Body (mirrors GeoDataFile.WriteGrid):
            //   origin.X, origin.Y, origin.Z (3 floats)
            //   cellSize (float), width (int32), depth (int32), maxStep (float)
            //   count (int32 = width*depth)
            //   per cell in row-major order: flags (byte) + height (float)
            w.Write(origin.x);
            w.Write(origin.y);
            w.Write(origin.z);
            w.Write(cellSize);
            w.Write(width);
            w.Write(depth);
            w.Write(maxStep);
            w.Write(count);
            for (var i = 0; i < count; i++)
            {
                w.Write(flags[i]);
                w.Write(heights[i]);
            }
        }

        /// <summary>
        /// Writes a multi-storey (Lineage-2-style) layered grid (file kind 3): each cell holds zero or more stacked
        /// walkable height layers, plus a full-height wall flag. Mirrors <c>GeoDataFile.WriteLayered</c> field-for-field.
        /// The layer arrays are CSR-packed: cell <c>i</c> owns layers <c>[cellStart[i], cellStart[i+1])</c>, cells in
        /// row-major order (cz outer, cx inner).
        /// </summary>
        /// <param name="stream">Destination stream (left open; caller owns it).</param>
        /// <param name="origin">Grid min corner (XZ anchor; layer heights carry real ground Y).</param>
        /// <param name="cellSize">Cell edge length in world units.</param>
        /// <param name="width">Cells along X.</param>
        /// <param name="depth">Cells along Z.</param>
        /// <param name="maxStep">Largest climbable step between adjacent cell layers.</param>
        /// <param name="layerMatchTolerance">How close (world Y) a query must be to a layer to count as standing on it.</param>
        /// <param name="cellStart">CSR offsets, length width*depth+1; cell i's layers are [cellStart[i], cellStart[i+1]).</param>
        /// <param name="layerHeights">Flat per-layer world Y, length = cellStart[width*depth].</param>
        /// <param name="layerWalkable">Flat per-layer walkable flag (1/0), same length/order as <paramref name="layerHeights"/>.</param>
        /// <param name="walls">Per-cell full-height wall flag, length width*depth, row-major.</param>
        public static void WriteLayeredGrid(
            Stream stream,
            Vector3 origin,
            float cellSize,
            int width,
            int depth,
            float maxStep,
            float layerMatchTolerance,
            int[] cellStart,
            float[] layerHeights,
            byte[] layerWalkable,
            bool[] walls)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (cellStart == null) throw new ArgumentNullException(nameof(cellStart));
            if (layerHeights == null) throw new ArgumentNullException(nameof(layerHeights));
            if (layerWalkable == null) throw new ArgumentNullException(nameof(layerWalkable));
            if (walls == null) throw new ArgumentNullException(nameof(walls));

            var count = width * depth;
            if (cellStart.Length != count + 1)
                throw new ArgumentException($"cellStart length {cellStart.Length} must equal width*depth+1 ({count + 1}).", nameof(cellStart));
            if (walls.Length != count)
                throw new ArgumentException($"walls length {walls.Length} must equal width*depth ({count}).", nameof(walls));
            if (layerHeights.Length != layerWalkable.Length)
                throw new ArgumentException("layerHeights and layerWalkable must be the same length.", nameof(layerWalkable));

            using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            // Header: magic(4) + version(1) + kind(3).
            w.Write(Magic);
            w.Write(Version);
            w.Write(KindLayeredGrid);

            // Body (mirrors GeoDataFile.WriteLayered):
            //   origin.X/Y/Z, cellSize, width, depth, maxStep, layerMatchTolerance
            //   per cell (row-major): wall (bool) + layerCount (int32) + layerCount * (height float, walkable byte)
            w.Write(origin.x);
            w.Write(origin.y);
            w.Write(origin.z);
            w.Write(cellSize);
            w.Write(width);
            w.Write(depth);
            w.Write(maxStep);
            w.Write(layerMatchTolerance);
            for (var i = 0; i < count; i++)
            {
                w.Write(walls[i]);
                var layerCount = cellStart[i + 1] - cellStart[i];
                w.Write(layerCount);
                for (var l = cellStart[i]; l < cellStart[i + 1]; l++)
                {
                    w.Write(layerHeights[l]);
                    w.Write(layerWalkable[l]);
                }
            }
        }

        /// <summary>
        /// Writes a nav-mesh (file kind 2): a flat vertex array plus a triangle index list (3 indices per triangle),
        /// matching <c>GeoDataFile.WriteNavMesh</c>.
        /// </summary>
        /// <param name="stream">Destination stream (left open; caller owns it).</param>
        /// <param name="verts">Vertex positions, written X/Y/Z component-wise as-is.</param>
        /// <param name="indices">Triangle vertex indices (length must be a multiple of 3).</param>
        public static void WriteNavMesh(Stream stream, Vector3[] verts, int[] indices)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (verts == null) throw new ArgumentNullException(nameof(verts));
            if (indices == null) throw new ArgumentNullException(nameof(indices));
            if (indices.Length % 3 != 0)
                throw new ArgumentException($"indices length {indices.Length} must be a multiple of 3 (whole triangles).", nameof(indices));

            using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            // Header: magic(4) + version(1) + kind(1).
            w.Write(Magic);
            w.Write(Version);
            w.Write(KindNavMesh);

            // Body (mirrors GeoDataFile.WriteNavMesh):
            //   vertCount (int32), then vertCount * (X,Y,Z floats)
            //   indexCount (int32), then indexCount * int32
            w.Write(verts.Length);
            for (var i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                w.Write(v.x);
                w.Write(v.y);
                w.Write(v.z);
            }
            w.Write(indices.Length);
            for (var i = 0; i < indices.Length; i++)
                w.Write(indices[i]);
        }
    }
}
