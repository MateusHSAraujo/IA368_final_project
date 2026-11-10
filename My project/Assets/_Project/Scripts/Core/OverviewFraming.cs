using System.Collections.Generic;
using UnityEngine;

namespace VRRobot.Core
{
    /// <summary>
    /// Computes an elevated viewpoint that frames a set of points (e.g. the objects of a scene being built):
    /// the eye sits outside the scene, above it, looking at its center along a horizontal direction.
    /// A VR head keeps looking horizontally (only the yaw is set), so seen from the eye the bounding sphere spans
    /// from (elevation - halfFov) to (elevation + halfFov) below the horizon. The defaults (20 + 20 = 40 degrees)
    /// keep it inside the lower half of a Quest 3 view (about 48 degrees).
    /// </summary>
    public static class OverviewFraming
    {
        public struct Result
        {
            /// <summary>Where the viewer's eyes should be.</summary>
            public Vector3 Eye;
            /// <summary>Center of the framed points.</summary>
            public Vector3 Target;
            /// <summary>Heading (degrees around +Y) that faces the target horizontally.</summary>
            public float Yaw;
            /// <summary>Radius of the sphere that encloses the points' bounds.</summary>
            public float Radius;
        }

        /// <param name="points">Points to frame. Empty input frames the origin.</param>
        /// <param name="viewDirection">Horizontal direction the viewer looks along (projected onto XZ).</param>
        /// <param name="elevationDeg">Angle above the horizon of the eye, seen from the target.</param>
        /// <param name="halfFovDeg">Half of the view angle the bounding sphere must fit in.</param>
        /// <param name="minDistance">Minimum eye-to-target distance.</param>
        public static Result Compute(IEnumerable<Vector3> points, Vector3 viewDirection, float elevationDeg = 20f,
            float halfFovDeg = 20f, float minDistance = 3f)
        {
            var bounds = new Bounds();
            bool any = false;
            foreach (var p in points)
            {
                if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                else bounds.Encapsulate(p);
            }

            var dir = Vector3.ProjectOnPlane(viewDirection, Vector3.up);
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;

            float radius = bounds.extents.magnitude;
            float distance = Mathf.Max(minDistance, radius / Mathf.Sin(Mathf.Clamp(halfFovDeg, 5f, 85f) * Mathf.Deg2Rad));
            float elevation = Mathf.Clamp(elevationDeg, 0f, 89f) * Mathf.Deg2Rad;
            var offset = -dir * (Mathf.Cos(elevation) * distance) + Vector3.up * (Mathf.Sin(elevation) * distance);

            return new Result
            {
                Eye = bounds.center + offset,
                Target = bounds.center,
                Yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg,
                Radius = radius,
            };
        }
    }
}
