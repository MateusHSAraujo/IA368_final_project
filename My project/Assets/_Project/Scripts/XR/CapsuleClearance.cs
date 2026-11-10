using UnityEngine;
using UnityEngine.AI;
using LocomotionCharacter = Oculus.Interaction.Locomotion.CharacterController;

namespace VRRobot.XR
{
    /// <summary>
    /// Physics checks for the player's capsule at a candidate feet position. The runtime NavMesh is only
    /// approximate near obstacles (voxel erosion and polygon simplification), so spawn and teleport targets
    /// are validated against the real colliders as well.
    /// </summary>
    public static class CapsuleClearance
    {
        /// <summary>Feet position of the character (bottom of the capsule, skin included).</summary>
        public static Vector3 FeetOf(LocomotionCharacter character) =>
            character.Pose.position + Vector3.down * (character.Height * 0.5f + character.SkinWidth);

        /// <summary>Capsule center that puts the character's feet at <paramref name="feet"/>.</summary>
        public static Vector3 CenterForFeet(LocomotionCharacter character, Vector3 feet) =>
            feet + Vector3.up * (character.Height * 0.5f + character.SkinWidth);

        /// <summary>true if the character's capsule, standing at <paramref name="feet"/>, touches no collider.</summary>
        public static bool IsClear(LocomotionCharacter character, Vector3 feet, float margin = 0.02f)
        {
            float radius = character.Radius + margin;
            float bottom = feet.y + character.SkinWidth + FloorTolerance; // ignore the floor itself
            float top = feet.y + character.Height + character.SkinWidth;
            Vector3 a = new Vector3(feet.x, bottom + radius, feet.z);
            Vector3 b = new Vector3(feet.x, Mathf.Max(bottom + radius, top - radius), feet.z);
            int n = Physics.OverlapCapsuleNonAlloc(a, b, radius, s_overlaps, character.LayerMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (s_overlaps[i].transform.root != character.transform.root) return false; // skip the player rig itself
            return true;
        }

        const float FloorTolerance = 0.05f;
        static readonly Collider[] s_overlaps = new Collider[16];

        /// <summary>
        /// Finds the clear NavMesh point closest to <paramref name="origin"/>, searching rings around it
        /// up to <paramref name="maxRadius"/>.
        /// </summary>
        public static bool TryFindClearPoint(LocomotionCharacter character, Vector3 origin, NavMeshQueryFilter filter,
            float maxRadius, out Vector3 point, float step = 0.1f)
        {
            for (float r = 0f; r <= maxRadius; r += step)
            {
                int samples = r <= 0f ? 1 : Mathf.Max(8, Mathf.CeilToInt(2f * Mathf.PI * r / step));
                for (int i = 0; i < samples; i++)
                {
                    float angle = i * Mathf.PI * 2f / samples;
                    Vector3 candidate = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * r;
                    if (NavMesh.SamplePosition(candidate, out var hit, step, filter) && IsClear(character, hit.position))
                    {
                        point = hit.position;
                        return true;
                    }
                }
            }
            point = origin;
            return false;
        }
    }
}
