using UnityEngine;

namespace VRRobot.Core
{
    /// <summary>
    /// Pure math for first-person teleoperation: thumbstick axes to a ROS <c>/cmd_vel_teleop</c> twist.
    /// Sign convention: stick forward (leftY &gt; 0) is forward motion (linear.x &gt; 0); stick left
    /// (rightX &lt; 0) is a counter-clockwise turn, i.e. positive ROS angular.z (REP-103, Z up).
    /// Unity clamps to the contract limits (there is no ROS-side clamp).
    /// </summary>
    public static class TeleopMath
    {
        /// <summary>Contract limit of |linear.x| in m/s.</summary>
        public const float MaxLinear = 0.5f;

        /// <summary>Contract limit of |angular.z| in rad/s.</summary>
        public const float MaxAngular = 1.5f;

        /// <summary>Velocities below this magnitude count as zero.</summary>
        public const float ZeroEpsilon = 1e-3f;

        /// <summary>
        /// Applies a radial-free (per-axis) deadzone and a response curve. The output is continuous: 0 at the
        /// deadzone edge, +/-1 at full deflection, then <c>sign * magnitude^exponent</c>.
        /// </summary>
        /// <param name="axis">Raw axis; clamped to [-1, 1].</param>
        /// <param name="deadzone">Deadzone radius; clamped to [0, 0.95].</param>
        /// <param name="exponent">Response curve exponent; clamped to [1, 3].</param>
        public static float ApplyDeadzone(float axis, float deadzone, float exponent = 1f)
        {
            axis = Mathf.Clamp(axis, -1f, 1f);
            deadzone = Mathf.Clamp(deadzone, 0f, 0.95f);
            exponent = Mathf.Clamp(exponent, 1f, 3f);
            float abs = Mathf.Abs(axis);
            if (abs <= deadzone) return 0f;
            float magnitude = (abs - deadzone) / (1f - deadzone);
            return Mathf.Sign(axis) * Mathf.Pow(magnitude, exponent);
        }

        /// <summary>
        /// Converts stick axes to a twist. <paramref name="leftY"/> drives linear.x; <paramref name="rightX"/>
        /// drives angular.z with inverted sign (stick left = counter-clockwise = positive).
        /// Maxima above the contract limits are clamped to <see cref="MaxLinear"/> / <see cref="MaxAngular"/>.
        /// </summary>
        public static void ToTwist(float leftY, float rightX, float maxLinear, float maxAngular,
            float deadzone, float exponent, out float linear, out float angular)
        {
            float lin = Mathf.Clamp(maxLinear, 0f, MaxLinear);
            float ang = Mathf.Clamp(maxAngular, 0f, MaxAngular);
            linear = Mathf.Clamp(ApplyDeadzone(leftY, deadzone, exponent) * lin, -MaxLinear, MaxLinear);
            angular = Mathf.Clamp(-ApplyDeadzone(rightX, deadzone, exponent) * ang, -MaxAngular, MaxAngular);
        }

        /// <summary>True when both velocities are below <see cref="ZeroEpsilon"/> in magnitude.</summary>
        public static bool IsZero(float linear, float angular) =>
            Mathf.Abs(linear) < ZeroEpsilon && Mathf.Abs(angular) < ZeroEpsilon;
    }
}
