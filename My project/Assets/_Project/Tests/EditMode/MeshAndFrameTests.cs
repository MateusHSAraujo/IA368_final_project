using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using VRRobot.Core;
using VRRobot.SimWorld.Building;
using VRRobot.SimWorld.Protocol;

namespace VRRobot.Tests
{
    public class MeshAndFrameTests
    {
        static byte[] F(params float[] v)
        {
            var b = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, b, 0, b.Length);
            return b;
        }

        static byte[] I(params int[] v)
        {
            var b = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, b, 0, b.Length);
            return b;
        }

        static MeshComponentDto Comp(int vOff, int vCount, int iOff, int iCount, int nOff, int nCount,
            int uvOff = 0, int uvCount = 0, int tex = -1, float r = 0.5f)
        {
            return new MeshComponentDto
            {
                vOff = vOff, vCount = vCount, iOff = iOff, iCount = iCount, nOff = nOff, nCount = nCount,
                uvOff = uvOff, uvCount = uvCount, tex = tex,
                colors = new[] { r, 0.5f, 0.5f, 0, 0, 0, 0, 0, 0 },
            };
        }

        // Square on the XY plane (RH) with 2 triangles, 4 vertices, all per-corner normals +Z.
        static ShapeMeshData Quad(bool smooth)
        {
            var verts = new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 };
            var inds = new[] { 0, 1, 2, 0, 2, 3 };
            var norms = Enumerable.Range(0, 6).SelectMany(_ => new float[] { 0, 0, 1 }).ToArray();
            if (!smooth) norms[3 * 3 + 0] = 0.5f; // corner 3 (vertex 0 of the 2nd triangle) with a different normal
            return new ShapeMeshData
            {
                header = new MeshHeader { h = 1, components = new[] { Comp(0, 12, 0, 6, 0, 18) } },
                vertices = verts, indices = inds, normals = norms, uvs = Array.Empty<float>(),
            };
        }

        [Test]
        public void Weld_MergesIdenticalCorners()
        {
            var b = ShapeMeshBuilder.Build(Quad(true));
            Assert.AreEqual(4, b.VertexCount);
            Assert.AreEqual(2, b.TriangleCount);
            Assert.AreEqual(6, b.SourceCorners);
            Assert.IsNull(b.Uvs);
            foreach (var n in b.Normals) Assert.That((n - Vector3.up).magnitude, Is.LessThan(1e-5f));
        }

        [Test]
        public void Weld_KeepsCornersWithDifferentNormals()
        {
            var b = ShapeMeshBuilder.Build(Quad(false));
            Assert.AreEqual(5, b.VertexCount);
        }

        [Test]
        public void Build_ConvertsAxesAndFlipsWinding()
        {
            var b = ShapeMeshBuilder.Build(Quad(true));
            var t = b.Submeshes[0];
            // 1st RH triangle (0,1,2) -> Unity (0,2,1); the geometric normal must agree with +Y
            var p = b.Positions;
            var n = Vector3.Cross(p[t[1]] - p[t[0]], p[t[2]] - p[t[0]]).normalized;
            Assert.That((n - Vector3.up).magnitude, Is.LessThan(1e-5f));
            Assert.That((p[t[0]] - CoordinateConverter.PositionToUnity(0, 0, 0)).magnitude, Is.LessThan(1e-6f));
        }

        [Test]
        public void Build_GroupsComponentsByMaterial()
        {
            // 3 components: two with the same color, one different -> 2 submeshes
            var verts = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            var allVerts = verts.Concat(verts).Concat(verts).ToArray();
            var inds = new[] { 0, 1, 2, 0, 1, 2, 0, 1, 2 };
            var d = new ShapeMeshData
            {
                header = new MeshHeader
                {
                    h = 1,
                    components = new[]
                    {
                        Comp(0, 9, 0, 3, 0, 0, r: 0.1f),
                        Comp(9, 9, 3, 3, 0, 0, r: 0.9f),
                        Comp(18, 9, 6, 3, 0, 0, r: 0.1f),
                    }
                },
                vertices = allVerts, indices = inds, normals = Array.Empty<float>(), uvs = Array.Empty<float>(),
            };
            var b = ShapeMeshBuilder.Build(d);
            Assert.AreEqual(2, b.Submeshes.Count);
            Assert.AreEqual(6, b.Submeshes[0].Length);
            Assert.AreEqual(3, b.Submeshes[1].Length);
            // no normals: face normal computed, converted to +Y
            foreach (var n in b.Normals) Assert.That((n - Vector3.up).magnitude, Is.LessThan(1e-5f));
        }

        [Test]
        public void Build_UsesPerCornerUvs()
        {
            var d = Quad(true);
            d.uvs = new float[] { 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1 };
            d.header.components[0].uvCount = 12;
            d.header.components[0].tex = 7;
            var b = ShapeMeshBuilder.Build(d);
            Assert.NotNull(b.Uvs);
            Assert.AreEqual(4, b.VertexCount);
            Assert.AreEqual(7, b.Materials[0].Texture);
        }

        [Test]
        public void Build_SkipsOutOfRangeIndices()
        {
            var d = Quad(true);
            d.indices[5] = 99;
            var b = ShapeMeshBuilder.Build(d);
            Assert.AreEqual(1, b.TriangleCount);
        }

        [Test]
        public void ParseMesh_DecodesFrames()
        {
            var header = "{\"h\":5,\"components\":[{\"vOff\":0,\"vCount\":9,\"iOff\":0,\"iCount\":3,\"nOff\":0,\"nCount\":9," +
                         "\"uvOff\":0,\"uvCount\":0,\"colors\":[1,0,0,0,0,0,0,0,0],\"transparency\":0.25,\"options\":1," +
                         "\"shadingAngle\":0.5,\"tex\":-1,\"texOptions\":0,\"texApplyMode\":0}]}";
            var frames = new[]
            {
                System.Text.Encoding.UTF8.GetBytes(header),
                F(0, 0, 0, 1, 0, 0, 0, 1, 0),
                I(0, 1, 2),
                F(0, 0, 1, 0, 0, 1, 0, 0, 1),
                Array.Empty<byte>(),
            };
            var d = FrameReader.ParseMesh(frames);
            Assert.AreEqual(5, d.header.h);
            Assert.AreEqual(9, d.vertices.Length);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, d.indices);
            Assert.AreEqual(0, d.uvs.Length);
            var k = new MaterialKey(d.header.components[0]);
            Assert.IsTrue(k.Transparent);
            Assert.IsTrue(k.BackfaceCulling);
            Assert.AreEqual(255, k.Diffuse.r);
        }

        [Test]
        public void ParseMesh_RejectsBadOffsets()
        {
            var header = "{\"h\":5,\"components\":[{\"vOff\":0,\"vCount\":90,\"iOff\":0,\"iCount\":3,\"nOff\":0,\"nCount\":0," +
                         "\"uvOff\":0,\"uvCount\":0,\"colors\":[1,0,0,0,0,0,0,0,0],\"tex\":-1}]}";
            var frames = new[]
            {
                System.Text.Encoding.UTF8.GetBytes(header), F(0, 0, 0), I(0, 1, 2), Array.Empty<byte>(), Array.Empty<byte>(),
            };
            Assert.Throws<SimProtocolException>(() => FrameReader.ParseMesh(frames));
        }

        [Test]
        public void ErrorFrame_Throws()
        {
            Assert.Throws<SimProtocolException>(() => FrameReader.ThrowIfError("{\"error\":\"not a shape: 3\"}"));
            Assert.DoesNotThrow(() => FrameReader.ThrowIfError("{\"h\":1}"));
        }

        [Test]
        public void FrameReader_RejectsTruncatedFrames()
        {
            Assert.Throws<FormatException>(() => FrameReader.ToFloats(new byte[5]));
            Assert.AreEqual(0, FrameReader.ToInts(null).Length);
        }
    }
}
