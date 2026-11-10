using NUnit.Framework;
using UnityEngine;
using VRRobot.Core;

namespace VRRobot.Tests
{
    public class OverviewFramingTests
    {
        static readonly Vector3[] Square =
        {
            new Vector3(-5, 0, -5), new Vector3(5, 0, -5), new Vector3(-5, 0, 5), new Vector3(5, 3, 5),
        };

        [Test]
        public void TargetIsBoundsCenter()
        {
            var r = OverviewFraming.Compute(Square, Vector3.forward);
            Assert.That(Vector3.Distance(r.Target, new Vector3(0, 1.5f, 0)), Is.LessThan(1e-4f));
        }

        [Test]
        public void EyeIsBehindAndAboveAtRequestedElevation()
        {
            var r = OverviewFraming.Compute(Square, Vector3.forward, elevationDeg: 35f);
            var toEye = r.Eye - r.Target;
            Assert.That(toEye.z, Is.LessThan(0f), "eye must be behind the target along the view direction");
            Assert.That(Mathf.Abs(toEye.x), Is.LessThan(1e-4f));
            float elevation = Mathf.Atan2(toEye.y, -toEye.z) * Mathf.Rad2Deg;
            Assert.That(elevation, Is.EqualTo(35f).Within(1e-3f));
        }

        [Test]
        public void BoundingSphereFitsInViewAngle()
        {
            var r = OverviewFraming.Compute(Square, Vector3.forward, halfFovDeg: 35f);
            float distance = Vector3.Distance(r.Eye, r.Target);
            float angle = Mathf.Asin(r.Radius / distance) * Mathf.Rad2Deg;
            Assert.That(angle, Is.LessThanOrEqualTo(35f + 1e-3f));
        }

        [Test]
        public void WithDefaultsSceneStaysWithinLowerFieldOfViewWhenLookingHorizontally()
        {
            var r = OverviewFraming.Compute(Square, Vector3.forward);
            var toTarget = r.Target - r.Eye;
            float distance = toTarget.magnitude;
            float centerBelow = Mathf.Asin(-toTarget.y / distance) * Mathf.Rad2Deg;
            float spread = Mathf.Asin(r.Radius / distance) * Mathf.Rad2Deg;
            Assert.That(centerBelow + spread, Is.LessThanOrEqualTo(40f + 1e-3f), "lowest point must stay within ~40 degrees below the horizon");
            Assert.That(centerBelow - spread, Is.GreaterThanOrEqualTo(-1e-3f), "highest point must not be above the horizon");
        }

        [Test]
        public void YawFacesViewDirectionIgnoringPitch()
        {
            var r = OverviewFraming.Compute(Square, new Vector3(1, -2, 0));
            Assert.That(r.Yaw, Is.EqualTo(90f).Within(1e-3f));
        }

        [Test]
        public void SmallOrEmptyInputUsesMinimumDistance()
        {
            var r = OverviewFraming.Compute(new Vector3[0], Vector3.forward, minDistance: 3f);
            Assert.That(r.Target, Is.EqualTo(Vector3.zero));
            Assert.That(Vector3.Distance(r.Eye, r.Target), Is.EqualTo(3f).Within(1e-4f));
        }
    }
}
