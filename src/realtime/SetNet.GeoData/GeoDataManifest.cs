using System;
using System.Collections.Generic;
using System.IO;

namespace SetNet.GeoData
{
    /// <summary>
    /// A portable index of a <b>sectored</b> world: a list of (sector id, relative <c>.geo</c> file path, world bounds).
    /// The Unity sector baker writes one of these alongside the per-sector <c>.geo</c> files; a headless server calls
    /// <see cref="Load"/> to reconstruct a single seamless <see cref="SectoredGeoData"/> over the whole world.
    /// </summary>
    /// <remarks>Binary format: magic "SNGM", version 1, then int32 count, then per entry: length-prefixed UTF-8 id,
    /// length-prefixed UTF-8 relative path, min(X,Y,Z) and max(X,Y,Z) floats.</remarks>
    public static class GeoDataManifest
    {
        private static readonly byte[] Magic = { (byte)'S', (byte)'N', (byte)'G', (byte)'M' };
        private const byte Version = 1;

        /// <summary>One sector entry in a manifest.</summary>
        public readonly struct Entry
        {
            /// <summary>Sector id.</summary>
            public readonly string Id;
            /// <summary>Path to the sector's <c>.geo</c> file, relative to the manifest file's directory.</summary>
            public readonly string RelativePath;
            /// <summary>The sector's world-space bounds.</summary>
            public readonly Bounds Bounds;

            /// <summary>Creates an entry.</summary>
            public Entry(string id, string relativePath, Bounds bounds) { Id = id; RelativePath = relativePath; Bounds = bounds; }
        }

        /// <summary>Writes a manifest to a stream.</summary>
        public static void Save(IReadOnlyList<Entry> entries, Stream stream)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write(Magic); w.Write(Version);
            w.Write(entries.Count);
            foreach (var e in entries)
            {
                w.Write(e.Id ?? "");
                w.Write(e.RelativePath ?? "");
                w.Write(e.Bounds.Min.X); w.Write(e.Bounds.Min.Y); w.Write(e.Bounds.Min.Z);
                w.Write(e.Bounds.Max.X); w.Write(e.Bounds.Max.Y); w.Write(e.Bounds.Max.Z);
            }
        }

        /// <summary>Reads a manifest's entries from a stream (does not load the referenced geodata).</summary>
        public static IReadOnlyList<Entry> ReadEntries(Stream stream)
        {
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
                throw new InvalidDataException("Not a GeoData manifest.");
            var version = r.ReadByte();
            if (version != Version) throw new InvalidDataException($"Unsupported manifest version {version}.");
            var count = r.ReadInt32();
            var entries = new List<Entry>(count);
            for (var i = 0; i < count; i++)
            {
                var id = r.ReadString();
                var path = r.ReadString();
                var min = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var max = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                entries.Add(new Entry(id, path, new Bounds(min, max)));
            }
            return entries;
        }

        /// <summary>
        /// Loads a manifest file and every <c>.geo</c> it references (paths resolved relative to the manifest's folder),
        /// returning a single <see cref="SectoredGeoData"/> spanning the whole world.
        /// </summary>
        public static SectoredGeoData Load(string manifestPath)
        {
            if (manifestPath == null) throw new ArgumentNullException(nameof(manifestPath));
            IReadOnlyList<Entry> entries;
            using (var fs = File.OpenRead(manifestPath))
                entries = ReadEntries(fs);

            var dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? "";
            var builder = new SectoredGeoDataBuilder();
            foreach (var e in entries)
            {
                var geoPath = Path.IsPathRooted(e.RelativePath) ? e.RelativePath : Path.Combine(dir, e.RelativePath);
                var geo = GeoDataFile.LoadFromFile(geoPath);
                builder.Add(e.Id, geo, e.Bounds);
            }
            return builder.Build();
        }
    }
}
