using NUnit.Framework;
using UnityEngine;
using VRRobot.Core;

namespace VRRobot.Tests
{
    public class PanelMathTests
    {
        [Test]
        public void ScaleFollowsDistanceRatioBothWays()
        {
            Assert.That(PanelMath.UniformScale(1f, 0.3f, 0.6f, 0.05f, 0.5f, 2.5f), Is.EqualTo(2f).Within(1e-5f));
            // Coming back to the start distance restores the initial scale (absolute, no accumulation).
            Assert.That(PanelMath.UniformScale(1f, 0.3f, 0.3f, 0.05f, 0.5f, 2.5f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(PanelMath.UniformScale(1.5f, 0.3f, 0.2f, 0.05f, 0.5f, 2.5f), Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void ScaleIsClamped()
        {
            Assert.That(PanelMath.UniformScale(1f, 0.3f, 3f, 0.05f, 0.5f, 2.5f), Is.EqualTo(2.5f));
            Assert.That(PanelMath.UniformScale(1f, 0.3f, 0f, 0.05f, 0.5f, 2.5f), Is.EqualTo(0.5f));
        }

        [Test]
        public void TinyStartDistanceKeepsScale()
        {
            Assert.That(PanelMath.UniformScale(1.2f, 0.001f, 0.5f, 0.05f, 0.5f, 2.5f), Is.EqualTo(1.2f));
            Assert.That(PanelMath.UniformScale(1.2f, 0f, 0.5f, 0.05f, 0.5f, 2.5f), Is.EqualTo(1.2f));
        }

        [Test]
        public void FitDistanceUsesTheTighterAngle()
        {
            // 0.5 m wide at 45 deg -> 0.25 / tan(22.5) = 0.6036 m.
            Assert.That(PanelMath.FitDistance(0.5f, 0.1f, 45f, 35f, 0.3f), Is.EqualTo(0.6036f).Within(1e-3f));
            // 0.5 m tall at 35 deg -> 0.25 / tan(17.5) = 0.7929 m.
            Assert.That(PanelMath.FitDistance(0.1f, 0.5f, 45f, 35f, 0.3f), Is.EqualTo(0.7929f).Within(1e-3f));
            Assert.That(PanelMath.FitDistance(0.01f, 0.01f, 45f, 35f, 0.3f), Is.EqualTo(0.3f));
        }

        [Test]
        public void TwistAngleExtractsRotationAboutAxis()
        {
            Assert.That(PanelMath.TwistAngle(Quaternion.Euler(0f, 30f, 0f), Vector3.up), Is.EqualTo(30f).Within(1e-3f));
            Assert.That(PanelMath.TwistAngle(Quaternion.Euler(0f, -50f, 0f), Vector3.up), Is.EqualTo(-50f).Within(1e-3f));
            Assert.That(PanelMath.TwistAngle(Quaternion.Euler(20f, 0f, 0f), Vector3.up), Is.EqualTo(0f).Within(1e-3f));
            // Yaw 40 then pitch 25 about the new right axis: the twist about that axis is the pitch.
            var right = Quaternion.Euler(0f, 40f, 0f) * Vector3.right;
            var q = Quaternion.AngleAxis(25f, right);
            Assert.That(PanelMath.TwistAngle(q, right), Is.EqualTo(25f).Within(1e-3f));
            Assert.That(PanelMath.TwistAngle(q, Vector3.up), Is.EqualTo(0f).Within(1e-3f));
        }

        [TestCase(0f, 0f)]
        [TestCase(35f, 45f)]
        [TestCase(-120f, 89.5f)]
        [TestCase(170f, 90f)]
        [TestCase(60f, -30f)]
        public void YawPitchRoundTrip(float yaw, float pitch)
        {
            PanelMath.YawPitch(Quaternion.Euler(pitch, yaw, 0f), out var y, out var p);
            Assert.That(p, Is.EqualTo(pitch).Within(0.05f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(y, yaw)), Is.LessThan(0.05f));
        }
    }
}
