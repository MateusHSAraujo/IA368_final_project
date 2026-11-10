using System;
using UnityEngine;
using UnityEngine.UI;

namespace VRRobot.XR
{
    /// <summary>
    /// Minimal VR menu: a world-space canvas with tabbed pages, opened in front of the head with the left
    /// Menu button. While open it holds the <see cref="LocomotionInputGate"/> and follows the motion of the player
    /// rig (fall after fly mode, recovery teleports, height changes), so it never stays behind out of view.
    /// </summary>
    public class VrMenuController : MonoBehaviour
    {
        [Serializable]
        public class Page
        {
            public string name;
            public Button tab;
            public GameObject root;
        }

        [SerializeField, Tooltip("World-space canvas root; inactive while closed.")] GameObject _panel;
        [SerializeField] Transform _head;
        [SerializeField, Tooltip("Camera Rig root; while open the panel moves with it.")] Transform _playerOrigin;
        [SerializeField] LocomotionInputGate _gate;
        [SerializeField] float _distance = 0.8f;
        [SerializeField] float _heightOffset = -0.1f;
        [SerializeField] Button _closeButton;
        [SerializeField] Page[] _pages;
        [SerializeField] OVRInput.Button _toggleButton = OVRInput.Button.Start;

        int _current;
        Vector3 _lastOriginPosition;
        float _lastOriginYaw;

        public bool IsOpen { get; private set; }

        void Awake()
        {
            if (_closeButton != null) _closeButton.onClick.AddListener(Close);
            for (int i = 0; i < _pages.Length; i++)
            {
                int index = i;
                if (_pages[i].tab != null) _pages[i].tab.onClick.AddListener(() => ShowPage(index));
            }
            _panel.SetActive(false);
        }

        void OnDisable()
        {
            if (IsOpen) Close();
        }

        void Update()
        {
            if (OVRInput.GetDown(_toggleButton)) Toggle();
        }

        // The locomotor re-places the rig late in the frame; a one-frame lag here is not noticeable.
        void LateUpdate()
        {
            if (!IsOpen || _playerOrigin == null) return;
            var delta = _playerOrigin.position - _lastOriginPosition;
            float yaw = _playerOrigin.eulerAngles.y;
            float yawDelta = Mathf.DeltaAngle(_lastOriginYaw, yaw);
            if (delta.sqrMagnitude > 1e-8f) _panel.transform.position += delta;
            if (Mathf.Abs(yawDelta) > 1e-3f) _panel.transform.RotateAround(_head.position, Vector3.up, yawDelta);
            TrackOrigin();
        }

        void TrackOrigin()
        {
            if (_playerOrigin == null) return;
            _lastOriginPosition = _playerOrigin.position;
            _lastOriginYaw = _playerOrigin.eulerAngles.y;
        }

        public void Toggle()
        {
            if (IsOpen) Close(); else Open();
        }

        public void Open()
        {
            if (IsOpen) return;
            var flat = Vector3.ProjectOnPlane(_head.forward, Vector3.up);
            flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;
            _panel.transform.SetPositionAndRotation(_head.position + flat * _distance + Vector3.up * _heightOffset,
                Quaternion.LookRotation(flat, Vector3.up));
            _panel.SetActive(true);
            IsOpen = true;
            TrackOrigin();
            _gate.Acquire(this);
            ShowPage(_current);
            Debug.Log("[XRLoco] Menu opened", this);
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            _panel.SetActive(false);
            _gate.Release(this);
            Debug.Log("[XRLoco] Menu closed", this);
        }

        public void ShowPage(int index)
        {
            if (index < 0 || index >= _pages.Length) return;
            _current = index;
            for (int i = 0; i < _pages.Length; i++)
            {
                if (_pages[i].root != null) _pages[i].root.SetActive(i == index);
                if (_pages[i].tab != null) _pages[i].tab.interactable = i != index;
            }
        }
    }
}
