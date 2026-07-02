using System.IO;
using UnityEngine;
using UnityEngine.AI;

namespace SetNet.GeoData.Unity.Editor
{
    /// <summary>
    /// Bakes a nav-mesh GeoData file from Unity's already-baked NavMesh via
    /// <see cref="NavMesh.CalculateTriangulation"/>. Zero manual work beyond having a NavMesh in the scene.
    /// </summary>
    public static class NavMeshGeoBaker
    {
        /// <summary>Summary of a nav-mesh bake.</summary>
        public readonly struct Result
        {
            /// <summary>Number of vertices written.</summary>
            public readonly int VertexCount;
            /// <summary>Number of triangles written.</summary>
            public readonly int TriangleCount;

            /// <summary>Creates a result.</summary>
            public Result(int vertexCount, int triangleCount)
            {
                VertexCount = vertexCount;
                TriangleCount = triangleCount;
            }
        }

        /// <summary>
        /// Triangulates the scene's baked NavMesh and writes it to <paramref name="outputPath"/> as a nav-mesh
        /// GeoData file. Returns <c>false</c> (with <paramref name="error"/> set) if no NavMesh is baked.
        /// </summary>
        public static bool Bake(string outputPath, out Result result, out string error)
        {
            result = default;
            error = null;

            var tri = NavMesh.CalculateTriangulation();
            if (tri.vertices == null || tri.vertices.Length == 0 || tri.indices == null || tri.indices.Length == 0)
            {
                error = "No baked NavMesh found in the scene. Bake one via Window > AI > Navigation (or a NavMeshSurface) first.";
                return false;
            }

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using (var fs = File.Create(outputPath))
                GeoDataFileWriter.WriteNavMesh(fs, tri.vertices, tri.indices);

            result = new Result(tri.vertices.Length, tri.indices.Length / 3);
            return true;
        }
    }
}
