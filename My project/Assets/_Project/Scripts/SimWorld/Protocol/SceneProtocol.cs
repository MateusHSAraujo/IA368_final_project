using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VRRobot.SimWorld.Protocol
{
    // DTOs of the ZMQ protocol v1 (see coppelia_scripts/README_COPPELIA_SETUP.md).
    // Public camelCase fields so they match the Lua JSON through JsonUtility.

    [Serializable]
    public class ErrorReply
    {
        public string error;
    }

    [Serializable]
    public class InfoReply
    {
        public int protocol;
        public string scene;
        public int objectCount;
        public int sceneObjectCount;
        public int shapeCount;
        public int sceneVersion;
        public int posePort;
        public int simulationState;
    }

    [Serializable]
    public class SceneObjectDto
    {
        public int h;
        public string name;
        public string path;
        public string type;
        public int parent;
        public float[] localPose;
        public bool visible;
        public bool isShape;
        public int components;
    }

    [Serializable]
    public class ListReply
    {
        public int sceneVersion;
        public SceneObjectDto[] objects;
    }

    [Serializable]
    public class MeshComponentDto
    {
        public int vOff, vCount;
        public int iOff, iCount;
        public int nOff, nCount;
        public int uvOff, uvCount;
        public float[] colors;      // 9 values: diffuse RGB, specular RGB, emission RGB
        public float transparency;  // 0 = opaque
        public int options;         // bit0: backface culling, bit1: wireframe
        public float shadingAngle;
        public int tex;             // -1 = no texture
        public int texOptions;      // bit0 repeat U, bit1 repeat V, bit2 interpolate
        public int texApplyMode;    // 0 modulate, 1 decal, 2 add
    }

    [Serializable]
    public class MeshHeader
    {
        public int h;
        public MeshComponentDto[] components;
    }

    [Serializable]
    public class TextureHeader
    {
        public int id, w, h;
    }

    [Serializable]
    public class PosesHeader
    {
        public int seq;
        public double t;
        public bool full;
        public int sceneVersion;
        public int count;
    }

    [Serializable]
    public class SceneEvent
    {
        public int sceneVersion;
    }

    /// <summary>Raw shape mesh as received from CoppeliaSim (shape RH frame).</summary>
    public class ShapeMeshData
    {
        public MeshHeader header;
        public float[] vertices;
        public int[] indices;
        public float[] normals;
        public float[] uvs;
    }

    public static class FrameReader
    {
        public static float[] ToFloats(byte[] data)
        {
            if (data == null || data.Length == 0) return Array.Empty<float>();
            if (data.Length % 4 != 0) throw new FormatException($"float frame with {data.Length} bytes");
            var r = new float[data.Length / 4];
            Buffer.BlockCopy(data, 0, r, 0, data.Length);
            if (!BitConverter.IsLittleEndian) SwapAll(r);
            return r;
        }

        public static int[] ToInts(byte[] data)
        {
            if (data == null || data.Length == 0) return Array.Empty<int>();
            if (data.Length % 4 != 0) throw new FormatException($"int32 frame with {data.Length} bytes");
            var r = new int[data.Length / 4];
            Buffer.BlockCopy(data, 0, r, 0, data.Length);
            if (!BitConverter.IsLittleEndian)
                for (int i = 0; i < r.Length; i++) r[i] = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(r[i]);
            return r;
        }

        static void SwapAll(float[] r)
        {
            var ints = MemoryMarshal.Cast<float, int>(r.AsSpan());
            for (int i = 0; i < ints.Length; i++) ints[i] = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(ints[i]);
        }

        public static string Text(byte[] data) => System.Text.Encoding.UTF8.GetString(data);

        /// <summary>Throws if the frame is an error {"error": ...}.</summary>
        public static void ThrowIfError(string json)
        {
            if (json.StartsWith("{\"error\"", StringComparison.Ordinal))
            {
                var e = JsonUtility.FromJson<ErrorReply>(json);
                throw new SimProtocolException(e.error);
            }
        }

        /// <summary>Decodes the reply to "mesh &lt;h&gt;": header + 4 binary frames.</summary>
        public static ShapeMeshData ParseMesh(byte[][] frames)
        {
            var head = Text(frames[0]);
            ThrowIfError(head);
            if (frames.Length != 5) throw new SimProtocolException($"mesh: expected 5 frames, got {frames.Length}");
            var d = new ShapeMeshData
            {
                header = JsonUtility.FromJson<MeshHeader>(head),
                vertices = ToFloats(frames[1]),
                indices = ToInts(frames[2]),
                normals = ToFloats(frames[3]),
                uvs = ToFloats(frames[4]),
            };
            d.header.components ??= Array.Empty<MeshComponentDto>();
            foreach (var c in d.header.components)
            {
                if (c.vOff + c.vCount > d.vertices.Length || c.iOff + c.iCount > d.indices.Length ||
                    c.nOff + c.nCount > d.normals.Length || c.uvOff + c.uvCount > d.uvs.Length)
                    throw new SimProtocolException($"mesh {d.header.h}: offsets outside the frames");
            }
            return d;
        }
    }

    public class SimProtocolException : Exception
    {
        public SimProtocolException(string msg) : base(msg) { }
    }
}
