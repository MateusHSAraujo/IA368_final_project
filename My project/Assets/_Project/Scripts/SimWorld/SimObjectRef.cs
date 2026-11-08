using UnityEngine;

namespace VRRobot.SimWorld
{
    /// <summary>Tags a rebuilt GameObject with its CoppeliaSim handle and path.</summary>
    [DisallowMultipleComponent]
    public class SimObjectRef : MonoBehaviour
    {
        public int handle;
        public string path;
        public string simType;
    }
}
