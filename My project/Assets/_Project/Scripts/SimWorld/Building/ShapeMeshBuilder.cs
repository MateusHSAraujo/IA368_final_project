using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VRRobot.Core;
using VRRobot.SimWorld.Protocol;

namespace VRRobot.SimWorld.Building
{
    /// <summary>Appearance of a group of components; components with the same key become one submesh.</summary>
    public readonly struct MaterialKey : IEquatable<MaterialKey>
    {
        public readonly Color32 Diffuse, Specular, Emission;
        public readonly byte Alpha;          // 255 = opaque
        public readonly int Texture;         // -1 = no texture
        public readonly int TexOptions, TexApplyMode;
        public readonly bool BackfaceCulling;

        public MaterialKey(MeshComponentDto c)
        {
            var k = c.colors != null && c.colors.Length >= 9 ? c.colors : new float[] { .8f, .8f, .8f, 0, 0, 0, 0, 0, 0 };
            Diffuse = ToColor(k, 0);
            Specular = ToColor(k, 3);
            Emission = ToColor(k, 6);
            Alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(1f - c.transparency) * 255f);
            Texture = c.tex;
            TexOptions = c.tex >= 0 ? c.texOptions : 0;
            TexApplyMode = c.tex >= 0 ? c.texApplyMode : 0;
            BackfaceCulling = (c.options & 1) != 0;
        }

        static Color32 ToColor(float[] k, int o) =>
            new Color32(B(k[o]), B(k[o + 1]), B(k[o + 2]), 255);

        static byte B(float v) => (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f);

        public bool Transparent => Alpha < 255;

        public bool Equals(MaterialKey o) =>
            Diffuse.Equals(o.Diffuse) && Specular.Equals(o.Specular) && Emission.Equals(o.Emission) &&
            Alpha == o.Alpha && Texture == o.Texture && TexOptions == o.TexOptions &&
            TexApplyMode == o.TexApplyMode && BackfaceCulling == o.BackfaceCulling;

        public override bool Equals(object obj) => obj is MaterialKey o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)(Diffuse.r | Diffuse.g << 8 | Diffuse.b << 16 | Alpha << 24);
                h = h * 31 + (Specular.r | Specular.g << 8 | Specular.b << 16);
                h = h * 31 + (Emission.r | Emission.g << 8 | Emission.b << 16);
                h = h * 31 + Texture;
                h = h * 31 + (TexOptions | TexApplyMode << 8 | (BackfaceCulling ? 1 << 16 : 0));
                return h;
            }
        }
    }

    /// <summary>Mesh already converted to Unity (LH frame), ready to become a Mesh.</summary>
    public class BuiltMesh
    {
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector2[] Uvs;           // null if no component has a texture
        public List<int[]> Submeshes = new List<int[]>();
        public List<MaterialKey> Materials = new List<MaterialKey>();
        public int SourceCorners;       // number of triangle corners received

        public int VertexCount => Positions.Length;

        public int TriangleCount
        {
            get { int n = 0; foreach (var s in Submeshes) n += s.Length / 3; return n; }
        }

        /// <summary>Creates the Mesh (main thread).</summary>
        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name };
            m.indexFormat = Positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.SetVertices(Positions);
            m.SetNormals(Normals);
            if (Uvs != null) m.SetUVs(0, Uvs);
            m.subMeshCount = Submeshes.Count;
            for (int i = 0; i < Submeshes.Count; i++) m.SetTriangles(Submeshes[i], i, false);
            m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>
    /// Converts a shape mesh (components with per-corner normals/UVs) into a welded Mesh:
    /// "de-indexes" every corner (position + normal + UV), welds equal corners with a hash, converts
    /// axes (RH Z-up -> LH Y-up), flips the winding and groups the components by material.
    /// Does not touch Unity APIs that require the main thread.
    /// </summary>
    public static class ShapeMeshBuilder
    {
        const float NormalQuant = 1e4f;
        const float UvQuant = 1e5f;

        readonly struct CornerKey : IEquatable<CornerKey>
        {
            readonly int _v, _nx, _ny, _nz, _u, _w;

            public CornerKey(int v, int nx, int ny, int nz, int u, int w)
            {
                _v = v; _nx = nx; _ny = ny; _nz = nz; _u = u; _w = w;
            }

            public bool Equals(CornerKey o) =>
                _v == o._v && _nx == o._nx && _ny == o._ny && _nz == o._nz && _u == o._u && _w == o._w;

            public override bool Equals(object obj) => obj is CornerKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = _v * 73856093;
                    h ^= _nx * 19349663;
                    h ^= _ny * 83492791;
                    h ^= _nz * 1000003;
                    h ^= _u * 1610612741;
                    h ^= _w * 805306457;
                    return h;
                }
            }
        }

        public static BuiltMesh Build(ShapeMeshData d)
        {
            var comps = d.header.components;
            bool anyUv = false;
            foreach (var c in comps) if (c.uvCount > 0) { anyUv = true; break; }

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = anyUv ? new List<Vector2>() : null;
            var groups = new Dictionary<MaterialKey, List<int>>();
            var order = new List<MaterialKey>();
            var map = new Dictionary<CornerKey, int>();
            var result = new BuiltMesh();

            foreach (var c in comps)
            {
                int corners = c.iCount - c.iCount % 3;
                if (corners == 0) continue;
                result.SourceCorners += corners;
                var key = new MaterialKey(c);
                if (!groups.TryGetValue(key, out var tris))
                {
                    tris = new List<int>();
                    groups[key] = tris;
                    order.Add(key);
                }

                map.Clear();
                bool hasN = c.nCount >= corners * 3;
                bool hasUv = c.uvCount >= corners * 2;
                int nVerts = c.vCount / 3;
                var tri = new int[3];

                for (int k = 0; k < corners; k += 3)
                {
                    Vector3 faceN = default;
                    if (!hasN) faceN = FaceNormal(d, c, k);
                    bool bad = false;
                    for (int j = 0; j < 3; j++)
                    {
                        int corner = k + j;
                        int vi = d.indices[c.iOff + corner];
                        if (vi < 0 || vi >= nVerts) { bad = true; break; }

                        Vector3 nrh;
                        if (hasN)
                        {
                            int no = c.nOff + corner * 3;
                            nrh = new Vector3(d.normals[no], d.normals[no + 1], d.normals[no + 2]);
                        }
                        else nrh = faceN;

                        float u = 0, w = 0;
                        if (hasUv)
                        {
                            int uo = c.uvOff + corner * 2;
                            u = d.uvs[uo];
                            w = d.uvs[uo + 1];
                        }

                        var ck = new CornerKey(vi,
                            Mathf.RoundToInt(nrh.x * NormalQuant), Mathf.RoundToInt(nrh.y * NormalQuant),
                            Mathf.RoundToInt(nrh.z * NormalQuant),
                            Mathf.RoundToInt(u * UvQuant), Mathf.RoundToInt(w * UvQuant));

                        if (!map.TryGetValue(ck, out int idx))
                        {
                            idx = positions.Count;
                            map[ck] = idx;
                            int vo = c.vOff + vi * 3;
                            positions.Add(CoordinateConverter.PositionToUnity(
                                d.vertices[vo], d.vertices[vo + 1], d.vertices[vo + 2]));
                            var nu = CoordinateConverter.PositionToUnity(nrh);
                            float len = nu.magnitude;
                            normals.Add(len > 1e-8f ? nu / len : Vector3.up);
                            uvs?.Add(new Vector2(u, w));
                        }
                        tri[j] = idx;
                    }
                    if (bad) continue;
                    // winding (i0,i1,i2) -> (i0,i2,i1)
                    tris.Add(tri[0]);
                    tris.Add(tri[2]);
                    tris.Add(tri[1]);
                }
            }

            result.Positions = positions.ToArray();
            result.Normals = normals.ToArray();
            result.Uvs = uvs?.ToArray();
            foreach (var k in order)
            {
                if (groups[k].Count == 0) continue;
                result.Submeshes.Add(groups[k].ToArray());
                result.Materials.Add(k);
            }
            return result;
        }

        static Vector3 FaceNormal(ShapeMeshData d, MeshComponentDto c, int k)
        {
            Vector3 P(int corner)
            {
                int vo = c.vOff + d.indices[c.iOff + corner] * 3;
                return new Vector3(d.vertices[vo], d.vertices[vo + 1], d.vertices[vo + 2]);
            }
            int nVerts = c.vCount / 3;
            for (int j = 0; j < 3; j++)
            {
                int vi = d.indices[c.iOff + k + j];
                if (vi < 0 || vi >= nVerts) return Vector3.forward;
            }
            var a = P(k);
            return Vector3.Cross(P(k + 1) - a, P(k + 2) - a).normalized; // RH frame
        }
    }
}
