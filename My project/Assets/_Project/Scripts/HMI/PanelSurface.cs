using UnityEngine;

namespace VRRobot.HMI
{
    /// <summary>Marker on the collider of the panel's map surface; points to the owning panel.</summary>
    public class PanelSurface : MonoBehaviour
    {
        public OperationsPanel Panel;
    }
}
