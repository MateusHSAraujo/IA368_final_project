using System;
using UnityEngine;
using VRRobot.Core;

namespace VRRobot.HMI
{
    /// <summary>Builds a single textured quad that covers an occupancy grid (outer corners), facing +Y in local space.</summary>
    public static class GridQuadMesh
    {
        static readonly Vector3[] Vertices = new Vector3[4];
        static readonly Vector2[] Uvs = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        static readonly Vector3[] Normals = { Vector3.up, Vector3.up, Vector3.up, Vector3.up };

        /// <param name="worldToLocal">Maps a Unity world point to the local space of the mesh owner.</param>
        /// <param name="height">World height (before <paramref name="worldToLocal"/>) of the quad.</param>
        public static void Build(Mesh mesh, GridInfo g, Func<Vector3, Vector3> worldToLocal, float height)
        {
            var size = g.Size;
            Vertices[0] = worldToLocal(MapGridMath.MapToWorld(g.Origin, height));
            Vertices[1] = worldToLocal(MapGridMath.MapToWorld(g.Origin + new Vector2(size.x, 0f), height));
            Vertices[2] = worldToLocal(MapGridMath.MapToWorld(g.Origin + new Vector2(0f, size.y), height));
            Vertices[3] = worldToLocal(MapGridMath.MapToWorld(g.Origin + size, height));

            // Unity front faces are clockwise: the face normal is cross(b - a, c - a).
            bool flip = Vector3.Cross(Vertices[1] - Vertices[0], Vertices[2] - Vertices[0]).y < 0f;
            mesh.Clear();
            mesh.vertices = Vertices;
            mesh.uv = Uvs;
            mesh.normals = Normals;
            mesh.triangles = flip ? new[] { 0, 2, 1, 2, 3, 1 } : new[] { 0, 1, 2, 2, 1, 3 };
            mesh.RecalculateBounds();
        }
    }
}
