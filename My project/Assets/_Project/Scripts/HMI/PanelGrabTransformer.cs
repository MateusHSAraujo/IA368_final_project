using Oculus.Interaction;
using UnityEngine;
using VRRobot.Core;

namespace VRRobot.HMI
{
    /// <summary>
    /// Frame grab of the operations panel (one or two grab points; only the first one drives it): moves the panel with
    /// the hand and applies the hand's rotation as yaw (about world up) and pitch (about the panel's horizontal axis),
    /// never roll, never scale. Yaw and pitch are accumulated as angles, so the pitch can be clamped to
    /// [<c>_minPitch</c>, <c>_maxPitch</c>] (0 = upright, 90 = lying face up) without the Euler-angle flips that make
    /// <c>GrabFreeTransformer</c>'s rotation constraints jump by 180 degrees past 90.
    /// </summary>
    public class PanelGrabTransformer : MonoBehaviour, ITransformer
    {
        [SerializeField] float _minPitch = 0f;
        [SerializeField] float _maxPitch = 90f;

        IGrabbable _grabbable;
        float _yaw, _pitch;
        Pose _prev;

        public void Initialize(IGrabbable grabbable) => _grabbable = grabbable;

        public void BeginTransform()
        {
            PanelMath.YawPitch(_grabbable.Transform.rotation, out _yaw, out _pitch);
            _pitch = Mathf.Clamp(_pitch, _minPitch, _maxPitch);
            _prev = _grabbable.GrabPoints[0];
        }

        public void UpdateTransform()
        {
            var t = _grabbable.Transform;
            var grab = _grabbable.GrabPoints[0];
            var delta = grab.rotation * Quaternion.Inverse(_prev.rotation);

            var right = Quaternion.Euler(0f, _yaw, 0f) * Vector3.right;
            _yaw += PanelMath.TwistAngle(delta, Vector3.up);
            _pitch = Mathf.Clamp(_pitch + PanelMath.TwistAngle(delta, right), _minPitch, _maxPitch);

            var oldRot = t.rotation;
            var newRot = Quaternion.Euler(_pitch, _yaw, 0f);
            // Follow the grab point's translation, then rotate about it (the frame stays under the hand).
            var pos = t.position + (grab.position - _prev.position);
            pos = grab.position + newRot * Quaternion.Inverse(oldRot) * (pos - grab.position);
            t.SetPositionAndRotation(pos, newRot);
            _prev = grab;
        }

        public void EndTransform() { }
    }
}
