using UnityEngine;

namespace VRRobot.Core
{
    /// <summary>
    /// Pure math for the operations panel: grab resize, the open distance that fits the panel in view, and the
    /// yaw/pitch orientation (roll-free) used by the frame grab.
    /// </summary>
    public static class PanelMath
    {
        /// <summary>
        /// Absolute uniform scale from a grab: <paramref name="initialScale"/> times <c>distance / startDistance</c>,
        /// clamped to [<paramref name="min"/>, <paramref name="max"/>]. A start closer than
        /// <paramref name="minStartDistance"/> to the centre keeps the initial scale (the ratio would be unstable).
        /// </summary>
        public static float UniformScale(float initialScale, float startDistance, float distance, float minStartDistance,
            float min, float max)
        {
            if (startDistance < minStartDistance || float.IsNaN(distance) || float.IsInfinity(distance))
                return Mathf.Clamp(initialScale, min, max);
            return Mathf.Clamp(initialScale * distance / startDistance, min, max);
        }

        /// <summary>
        /// Distance at which a flat panel facing the viewer, <paramref name="width"/> × <paramref name="height"/> m,
        /// fits inside the horizontal and vertical view angles (degrees, full angles), never below
        /// <paramref name="minDistance"/>.
        /// </summary>
        public static float FitDistance(float width, float height, float horizontalFovDeg, float verticalFovDeg,
            float minDistance)
        {
            float dh = 0.5f * width / Mathf.Tan(0.5f * horizontalFovDeg * Mathf.Deg2Rad);
            float dv = 0.5f * height / Mathf.Tan(0.5f * verticalFovDeg * Mathf.Deg2Rad);
            return Mathf.Max(minDistance, Mathf.Max(dh, dv));
        }

        /// <summary>
        /// Signed angle (degrees, -180..180) of the twist of <paramref name="q"/> about the unit
        /// <paramref name="axis"/> (swing-twist decomposition).
        /// </summary>
        public static float TwistAngle(Quaternion q, Vector3 axis)
        {
            float proj = q.x * axis.x + q.y * axis.y + q.z * axis.z;
            if (Mathf.Abs(proj) < 1e-9f && Mathf.Abs(q.w) < 1e-9f) return 0f;
            float deg = 2f * Mathf.Atan2(proj, q.w) * Mathf.Rad2Deg;
            return Mathf.DeltaAngle(0f, deg);
        }

        /// <summary>
        /// Yaw and pitch (degrees) of a roll-free panel rotation <c>Quaternion.Euler(pitch, yaw, 0)</c>, with pitch
        /// in -90..90. Unlike <c>eulerAngles</c>, it stays unambiguous near +-90 (pitch 90 = lying face up): the yaw
        /// comes from the horizontal part of the forward axis, or of the up axis when the forward is near vertical.
        /// </summary>
        public static void YawPitch(Quaternion rotation, out float yaw, out float pitch)
        {
            var f = rotation * Vector3.forward;
            pitch = Mathf.Asin(Mathf.Clamp(-f.y, -1f, 1f)) * Mathf.Rad2Deg;
            var h = new Vector2(f.x, f.z);
            if (h.sqrMagnitude < 0.25f)
            {
                // Forward near vertical: up = Ry * (0, cos p, sin p), its horizontal part points along the yaw
                // (sign of sin p).
                var u = rotation * Vector3.up;
                float s = Mathf.Sign(pitch);
                h = new Vector2(u.x * s, u.z * s);
            }
            yaw = Mathf.Atan2(h.x, h.y) * Mathf.Rad2Deg;
        }
    }
}
