using UnityEngine;
using UnityEngine.InputSystem;

namespace VRRobot.Desktop
{
    /// <summary>
    /// Free-fly camera for testing on the PC (replaced by the XR Origin in the VR version).
    /// Hold the right mouse button to look around; WASD moves, Q/E goes down/up,
    /// Shift speeds up, the mouse wheel changes the speed.
    /// </summary>
    public class DesktopFlyCamera : MonoBehaviour
    {
        public float speed = 2.5f;
        public float fastMultiplier = 4f;
        public float lookSensitivity = 0.12f;

        float _yaw, _pitch;

        void OnEnable()
        {
            var e = transform.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180f ? e.x - 360f : e.x;
        }

        void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            bool looking = mouse.rightButton.isPressed;
            Cursor.lockState = looking ? CursorLockMode.Locked : CursorLockMode.None;
            if (looking)
            {
                var d = mouse.delta.ReadValue();
                _yaw += d.x * lookSensitivity;
                _pitch = Mathf.Clamp(_pitch - d.y * lookSensitivity, -89f, 89f);
                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f) speed = Mathf.Clamp(speed * (scroll > 0 ? 1.15f : 1f / 1.15f), 0.1f, 50f);

            var move = Vector3.zero;
            if (kb.wKey.isPressed) move += Vector3.forward;
            if (kb.sKey.isPressed) move += Vector3.back;
            if (kb.dKey.isPressed) move += Vector3.right;
            if (kb.aKey.isPressed) move += Vector3.left;
            var vertical = 0f;
            if (kb.eKey.isPressed) vertical += 1f;
            if (kb.qKey.isPressed) vertical -= 1f;
            float s = speed * (kb.leftShiftKey.isPressed ? fastMultiplier : 1f) * Time.unscaledDeltaTime;
            transform.position += transform.TransformDirection(move) * s + Vector3.up * (vertical * s);
        }

        void OnDisable()
        {
            Cursor.lockState = CursorLockMode.None;
        }
    }
}
