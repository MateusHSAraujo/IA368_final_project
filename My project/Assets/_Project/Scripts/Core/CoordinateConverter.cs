using UnityEngine;

namespace VRRobot.Core
{
    /// <summary>
    /// Conversion between the CoppeliaSim/ROS frame (right-handed, Z up) and the Unity frame
    /// (left-handed, Y up). Same origin, no scaling.
    ///   position:   (x, y, z)        -> (-y, z, x)
    ///   quaternion: (qx, qy, qz, qw) -> (qy, -qz, -qx, qw)
    ///   triangle:   (i0, i1, i2)     -> (i0, i2, i1)
    /// The same position rule applies to vertices and normals in any local frame.
    /// </summary>
    public static class CoordinateConverter
    {
        public static Vector3 PositionToUnity(float x, float y, float z) => new Vector3(-y, z, x);

        public static Vector3 PositionToUnity(Vector3 rh) => new Vector3(-rh.y, rh.z, rh.x);

        public static Vector3 PositionFromUnity(Vector3 u) => new Vector3(u.z, -u.x, u.y);

        public static Quaternion RotationToUnity(float qx, float qy, float qz, float qw) =>
            new Quaternion(qy, -qz, -qx, qw);

        public static Quaternion RotationFromUnity(Quaternion u) => new Quaternion(-u.z, u.x, -u.y, u.w);

        /// <summary>CoppeliaSim pose {x,y,z,qx,qy,qz,qw} starting at <paramref name="offset"/>.</summary>
        public static void PoseToUnity(float[] pose, int offset, out Vector3 position, out Quaternion rotation)
        {
            position = PositionToUnity(pose[offset], pose[offset + 1], pose[offset + 2]);
            rotation = RotationToUnity(pose[offset + 3], pose[offset + 4], pose[offset + 5], pose[offset + 6]);
            rotation = Normalize(rotation);
        }

        /// <summary>Flips the triangle winding in place (swaps the last two indices).</summary>
        public static void FlipWinding(int[] indices)
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            }
        }

        static Quaternion Normalize(Quaternion q)
        {
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (n < 1e-8f) return Quaternion.identity;
            return new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n);
        }
    }
}
