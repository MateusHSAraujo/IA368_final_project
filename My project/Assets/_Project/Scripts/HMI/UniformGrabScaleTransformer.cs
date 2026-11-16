using Oculus.Interaction;
using UnityEngine;
using VRRobot.Core;

namespace VRRobot.HMI
{
    /// <summary>
    /// One-grab uniform scale for the operations panel's corner handles. A uniform variant of Meta's
    /// <c>OneGrabScaleTransformer</c> (PanelWithManipulators sample): the scale is absolute, the initial scale times the
    /// ratio between the grab point's in-plane distance from the panel centre now and at grab start. Nothing is
    /// accumulated frame to frame, so the scale follows the hand both ways and never winds up past the limits (unlike
    /// <c>GrabFreeTransformer</c>'s two-grab scale with distance grab, whose grab points start coincident).
    /// </summary>
    public class UniformGrabScaleTransformer : MonoBehaviour, ITransformer
    {
        [SerializeField] float _minScale = 0.5f;
        [SerializeField] float _maxScale = 2.5f;
        [SerializeField, Tooltip("Grab starts closer than this to the centre (m, in the panel plane) do not scale.")]
        float _minStartDistance = 0.05f;

        IGrabbable _grabbable;
        float _initialScale = 1f;
        float _startDistance;

        public void Initialize(IGrabbable grabbable) => _grabbable = grabbable;

        public void BeginTransform()
        {
            var t = _grabbable.Transform;
            _initialScale = t.localScale.x;
            _startDistance = PlaneDistance(t, _grabbable.GrabPoints[0].position);
        }

        public void UpdateTransform()
        {
            var t = _grabbable.Transform;
            float d = PlaneDistance(t, _grabbable.GrabPoints[0].position);
            float s = PanelMath.UniformScale(_initialScale, _startDistance, d, _minStartDistance, _minScale, _maxScale);
            t.localScale = Vector3.one * s;
        }

        public void EndTransform() { }

        /// <summary>
        /// Distance from the panel centre in the panel plane, in meters (unscaled). The panel root is upright, so the
        /// plane is its local XY (see <see cref="OperationsPanel"/>).
        /// </summary>
        static float PlaneDistance(Transform t, Vector3 world)
        {
            var local = Quaternion.Inverse(t.rotation) * (world - t.position);
            return new Vector2(local.x, local.y).magnitude;
        }
    }
}
