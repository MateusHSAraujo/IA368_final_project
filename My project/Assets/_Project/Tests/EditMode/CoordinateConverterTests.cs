using NUnit.Framework;
using UnityEngine;
using VRRobot.Core;

namespace VRRobot.Tests
{
    public class CoordinateConverterTests
    {
        const float Eps = 1e-5f;

        static void AssertVec(Vector3 expected, Vector3 actual, float eps = Eps)
        {
            Assert.That((expected - actual).magnitude, Is.LessThan(eps), $"expected {expected:F5}, got {actual:F5}");
        }

        [Test]
        public void Position_AxesMapToUnity()
        {
            // CoppeliaSim X (forward) -> Unity +Z; Y (left) -> -X; Z (up) -> +Y
            AssertVec(new Vector3(0, 0, 1), CoordinateConverter.PositionToUnity(1, 0, 0));
            AssertVec(new Vector3(-1, 0, 0), CoordinateConverter.PositionToUnity(0, 1, 0));
            AssertVec(new Vector3(0, 1, 0), CoordinateConverter.PositionToUnity(0, 0, 1));
            AssertVec(new Vector3(-2, 3, 1), CoordinateConverter.PositionToUnity(1, 2, 3));
        }

        [Test]
        public void Position_RoundTrip()
        {
            var p = new Vector3(0.3f, -1.7f, 2.2f);
            AssertVec(p, CoordinateConverter.PositionFromUnity(CoordinateConverter.PositionToUnity(p)));
        }

        // RH rotation matrix from axis-angle (Rodrigues), applied to a vector.
        static Vector3 RotateRH(Vector3 axis, float angleRad, Vector3 v)
        {
            axis.Normalize();
            float c = Mathf.Cos(angleRad), s = Mathf.Sin(angleRad);
            return v * c + Vector3.Cross(axis, v) * s + axis * (Vector3.Dot(axis, v) * (1 - c));
        }

        static readonly Vector3[] Axes =
        {
            new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1),
            new Vector3(1, 2, 3), new Vector3(-0.5f, 0.2f, 0.9f),
        };

        static readonly float[] Angles = { 0.3f, 1.2f, -2.5f, Mathf.PI / 2 };

        [Test]
        public void Rotation_MatchesRotatedVectorsInBothFrames()
        {
            var probes = new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0.3f, -0.4f, 0.8f) };
            foreach (var axis in Axes)
            foreach (var ang in Angles)
            {
                var a = axis.normalized;
                float s = Mathf.Sin(ang / 2);
                // CoppeliaSim RH quaternion (qx,qy,qz,qw)
                var qu = CoordinateConverter.RotationToUnity(a.x * s, a.y * s, a.z * s, Mathf.Cos(ang / 2));
                foreach (var v in probes)
                {
                    var rotatedRh = RotateRH(a, ang, v);
                    var expected = CoordinateConverter.PositionToUnity(rotatedRh);
                    var actual = qu * CoordinateConverter.PositionToUnity(v);
                    AssertVec(expected, actual, 1e-4f);
                }
            }
        }

        [Test]
        public void Rotation_RoundTrip()
        {
            var q = new Quaternion(0.1f, 0.2f, 0.3f, 0.927f);
            var u = CoordinateConverter.RotationToUnity(q.x, q.y, q.z, q.w);
            var back = CoordinateConverter.RotationFromUnity(u);
            Assert.AreEqual(q.x, back.x, Eps);
            Assert.AreEqual(q.y, back.y, Eps);
            Assert.AreEqual(q.z, back.z, Eps);
            Assert.AreEqual(q.w, back.w, Eps);
        }

        [Test]
        public void Pose_ComposesLikeParentChild()
        {
            // parent at (1,0,0) rotated 90 deg about Z; child at local (1,0,0) -> RH world (1,1,0)
            float h = Mathf.Sqrt(0.5f);
            var parent = new float[] { 1, 0, 0, 0, 0, h, h };
            var child = new float[] { 1, 0, 0, 0, 0, 0, 1 };
            CoordinateConverter.PoseToUnity(parent, 0, out var pp, out var pr);
            CoordinateConverter.PoseToUnity(child, 0, out var cp, out _);
            var world = pp + pr * cp;
            AssertVec(CoordinateConverter.PositionToUnity(1, 1, 0), world, 1e-5f);
        }

        [Test]
        public void Winding_FlipKeepsFrontFaceAfterMirror()
        {
            // CCW triangle seen from +Z in RH (normal +Z). After conversion the normal becomes +Y in Unity;
            // Unity treats as front the face with clockwise winding seen from outside (normal = cross(b-a, c-a)
            // in LH points to the side from which the winding looks clockwise).
            var a = new Vector3(0, 0, 0);
            var b = new Vector3(1, 0, 0);
            var c = new Vector3(0, 1, 0);
            var idx = new[] { 0, 1, 2 };
            CoordinateConverter.FlipWinding(idx);
            CollectionAssert.AreEqual(new[] { 0, 2, 1 }, idx);
            var v = new[] { a, b, c };
            var ua = CoordinateConverter.PositionToUnity(v[idx[0]]);
            var ub = CoordinateConverter.PositionToUnity(v[idx[1]]);
            var uc = CoordinateConverter.PositionToUnity(v[idx[2]]);
            // Unity geometric normal (left-hand rule): cross(b-a, c-a), as RecalculateNormals does.
            var n = Vector3.Cross(ub - ua, uc - ua).normalized;
            AssertVec(Vector3.up, n);
        }
    }
}
