using NUnit.Framework;
using VRRobot.Core;

namespace VRRobot.Tests
{
    public class TeleopMathTests
    {
        const float Tol = 1e-5f;

        [Test]
        public void InsideDeadzoneIsZero()
        {
            Assert.That(TeleopMath.ApplyDeadzone(0f, 0.2f), Is.EqualTo(0f));
            Assert.That(TeleopMath.ApplyDeadzone(0.19f, 0.2f), Is.EqualTo(0f));
            Assert.That(TeleopMath.ApplyDeadzone(-0.2f, 0.2f), Is.EqualTo(0f));
        }

        [Test]
        public void JustOutsideDeadzoneIsSmallPositive()
        {
            float v = TeleopMath.ApplyDeadzone(0.21f, 0.2f);
            Assert.That(v, Is.GreaterThan(0f).And.LessThan(0.05f));
            Assert.That(TeleopMath.ApplyDeadzone(-0.21f, 0.2f), Is.EqualTo(-v).Within(Tol));
        }

        [Test]
        public void FullDeflectionIsUnit()
        {
            Assert.That(TeleopMath.ApplyDeadzone(1f, 0.2f), Is.EqualTo(1f).Within(Tol));
            Assert.That(TeleopMath.ApplyDeadzone(-1f, 0.2f), Is.EqualTo(-1f).Within(Tol));
            Assert.That(TeleopMath.ApplyDeadzone(1f, 0.2f, 2f), Is.EqualTo(1f).Within(Tol));
        }

        [Test]
        public void ContinuousAtDeadzoneEdge()
        {
            float v = TeleopMath.ApplyDeadzone(0.2f + 1e-4f, 0.2f);
            Assert.That(v, Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void ExponentSoftensMidRange()
        {
            float linear = TeleopMath.ApplyDeadzone(0.5f, 0f, 1f);
            float squared = TeleopMath.ApplyDeadzone(0.5f, 0f, 2f);
            Assert.That(squared, Is.LessThan(linear));
            Assert.That(squared, Is.EqualTo(0.25f).Within(Tol));
        }

        [Test]
        public void InputBeyondUnitIsClamped()
        {
            Assert.That(TeleopMath.ApplyDeadzone(5f, 0.2f), Is.EqualTo(1f).Within(Tol));
            Assert.That(TeleopMath.ApplyDeadzone(-5f, 0.2f), Is.EqualTo(-1f).Within(Tol));
        }

        [Test]
        public void TwistSignConvention()
        {
            TeleopMath.ToTwist(1f, 0f, 0.5f, 1.5f, 0.1f, 1f, out float lin, out float ang);
            Assert.That(lin, Is.EqualTo(0.5f).Within(Tol));
            Assert.That(ang, Is.EqualTo(0f));

            TeleopMath.ToTwist(0f, -1f, 0.5f, 1.5f, 0.1f, 1f, out lin, out ang);
            Assert.That(lin, Is.EqualTo(0f));
            Assert.That(ang, Is.EqualTo(1.5f).Within(Tol));

            TeleopMath.ToTwist(0f, 1f, 0.5f, 1.5f, 0.1f, 1f, out lin, out ang);
            Assert.That(ang, Is.EqualTo(-1.5f).Within(Tol));

            TeleopMath.ToTwist(-1f, 0f, 0.5f, 1.5f, 0.1f, 1f, out lin, out _);
            Assert.That(lin, Is.EqualTo(-0.5f).Within(Tol));
        }

        [Test]
        public void MaximaAboveContractAreClamped()
        {
            TeleopMath.ToTwist(1f, -1f, 2f, 5f, 0.1f, 1f, out float lin, out float ang);
            Assert.That(lin, Is.EqualTo(TeleopMath.MaxLinear).Within(Tol));
            Assert.That(ang, Is.EqualTo(TeleopMath.MaxAngular).Within(Tol));
            TeleopMath.ToTwist(-1f, 1f, 2f, 5f, 0.1f, 1f, out lin, out ang);
            Assert.That(lin, Is.EqualTo(-TeleopMath.MaxLinear).Within(Tol));
            Assert.That(ang, Is.EqualTo(-TeleopMath.MaxAngular).Within(Tol));
        }

        [Test]
        public void IsZeroDetection()
        {
            TeleopMath.ToTwist(0f, 0f, 0.5f, 1.5f, 0.15f, 1f, out float lin, out float ang);
            Assert.IsTrue(TeleopMath.IsZero(lin, ang));
            TeleopMath.ToTwist(0.1f, -0.1f, 0.5f, 1.5f, 0.15f, 1f, out lin, out ang);
            Assert.IsTrue(TeleopMath.IsZero(lin, ang));

            TeleopMath.ToTwist(0.3f, 0f, 0.5f, 1.5f, 0.15f, 1f, out lin, out ang);
            Assert.IsFalse(TeleopMath.IsZero(lin, ang));
            TeleopMath.ToTwist(0f, 0.3f, 0.5f, 1.5f, 0.15f, 1f, out lin, out ang);
            Assert.IsFalse(TeleopMath.IsZero(lin, ang));
            Assert.IsFalse(TeleopMath.IsZero(0.002f, 0f));
        }
    }
}
