using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SetNet.GeoData.Unity.Editor
{
    /// <summary>
    /// Writes a sectored-world manifest (magic "SNGM") listing each sector's id, relative <c>.geo</c> path and world
    /// bounds. A headless server loads it with <c>GeoDataManifest.Load(path)</c> to rebuild one seamless
    /// <c>SectoredGeoData</c>. Mirrors <c>GeoDataManifest.Save</c> / <c>ReadEntries</c> byte-for-byte.
    /// </summary>
    public static class GeoDataManifestWriter
    {
        private static readonly byte[] Magic = { (byte)'S', (byte)'N', (byte)'G', (byte)'M' };
        private const byte Version = 1;

        /// <summary>One sector entry: id, path (relative to the manifest folder), and world bounds.</summary>
        public struct Entry
        {
            /// <summary>Sector id (e.g. "x0_z1").</summary>
            public string Id;
            /// <summary>Path to the sector's <c>.geo</c> file, relative to the manifest's directory.</summary>
            public string RelativePath;
            /// <summary>The sector's world-space bounds.</summary>
            public Bounds Bounds;
        }

        /// <summary>Writes a manifest to a stream.</summary>
        public static void Write(Stream stream, IList<Entry> entries)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (entries == null) throw new ArgumentNullException(nameof(entries));

            using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write(Magic);
            w.Write(Version);
            w.Write(entries.Count);
            foreach (var e in entries)
            {
                w.Write(e.Id ?? "");
                w.Write(e.RelativePath ?? "");
                w.Write(e.Bounds.min.x); w.Write(e.Bounds.min.y); w.Write(e.Bounds.min.z);
                w.Write(e.Bounds.max.x); w.Write(e.Bounds.max.y); w.Write(e.Bounds.max.z);
            }
        }

        /// <summary>Writes a manifest to a file path.</summary>
        public static void WriteToFile(string path, IList<Entry> entries)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = File.Create(path);
            Write(fs, entries);
        }
    }
}
