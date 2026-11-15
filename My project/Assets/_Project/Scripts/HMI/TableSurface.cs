using UnityEngine;

namespace VRRobot.HMI
{
    /// <summary>Marker on the collider of the table's map surface; points to the owning table.</summary>
    public class TableSurface : MonoBehaviour
    {
        public HolographicTacticalTable Table;
    }
}
