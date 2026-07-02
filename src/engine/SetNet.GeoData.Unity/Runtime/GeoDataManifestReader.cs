using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SetNet.GeoData.Unity
{
    /// <summary>One sector entry read from a <c>.geomap</c> manifest.</summary>
    public struct GeoManifestEntry
    {
        /// <summary>Sector id.</summary>
        public string Id;
        /// <summary>Path to the sector's <c>.geo</c> file, relative to the manifest's folder.</summary>
        public string RelativePath;
        /// <summary>The sector's world-space bounds.</summary>
        public Bounds Bounds;
    }

    /// <summary>Reads a sectored-world manifest (magic "SNGM") for the debug visualizer. Mirrors <c>GeoDataManifest</c>.</summary>
    public static class GeoDataManifestReader
    {
        /// <summary>Parses a manifest's entries from raw bytes.</summary>
        public static List<GeoManifestEntry> Read(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using var ms = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);

            var magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != 'S' || magic[1] != 'N' || magic[2] != 'G' || magic[3] != 'M')
                throw new InvalidDataException("Not a GeoData manifest.");
            var version = r.ReadByte();
            if (version != 1) throw new InvalidDataException($"Unsupported manifest version {version}.");
            var count = r.ReadInt32();
            var entries = new List<GeoManifestEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var id = r.ReadString();
                var path = r.ReadString();
                var min = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var max = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var b = new Bounds();
                b.SetMinMax(min, max);
                entries.Add(new GeoManifestEntry { Id = id, RelativePath = path, Bounds = b });
            }
            return entries;
        }

        /// <summary>Parses a manifest from a file path.</summary>
        public static List<GeoManifestEntry> ReadFile(string path) => Read(File.ReadAllBytes(path));
    }
}
