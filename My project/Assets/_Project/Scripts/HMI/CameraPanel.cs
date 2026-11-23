using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VRRobot.Core;
using VRRobot.XR;

namespace VRRobot.HMI
{
    /// <summary>
    /// Camera panel of the teleop mode (Stage 6): the robot's front camera on a floating screen with the teleop and
    /// mission state, Resume mission and Close. It opens and closes with <see cref="FirstPersonTeleopController"/>
    /// (X or the Mission page); Close ends the teleop mode.
    /// </summary>
    /// <remarks>
    /// Same family as <see cref="OperationsPanel"/>: the root stays active, only <c>_content</c>, <c>_toolbar</c> and the
    /// <c>_activeWhileOpen</c> objects are toggled; it opens upright in front of the head and then stays fixed in the
    /// world; the frame bars are trigger ray grabs driven by <see cref="PanelGrabTransformer"/> (move, yaw, pitch). The
    /// visuals hang under <c>_pivot</c> (rotated -90 degrees about X: pivot +Y towards the viewer, +Z up). Every visual
    /// is an object of the prefab; this script only feeds the texture and the texts. The JPEG rows are top-down
    /// (the bridge flips CoppeliaSim's image), which is what <see cref="ImageConversion.LoadImage(Texture2D, byte[])"/>
    /// expects.
    /// </remarks>
    public class CameraPanel : MonoBehaviour
    {
        const string LogPrefix = "[HMI] ";
        const float FrameHeight = 0.02f;

        [Header("Scene references (fall back to FindFirstObjectByType)")]
        [SerializeField, Tooltip("CenterEyeAnchor.")] Transform _head;
        [SerializeField] FirstPersonTeleopController _teleop;

        [Header("Prefab parts")]
        [SerializeField, Tooltip("Child of the root, rotated -90 degrees about X; parent of the content and toolbar.")]
        Transform _pivot;
        [SerializeField] Transform _content;
        [SerializeField, Tooltip("Quad (rotated 90 degrees about X) that shows the camera image.")] MeshRenderer _screen;
        [SerializeField, Tooltip("Dark plate behind the screen; what a panel without image shows.")] Transform _backdrop;
        [SerializeField, Tooltip("Frame bars bottom, top, left, right: trigger BoxCollider + ColliderSurface + RayInteractable.")]
        Transform[] _bars = new Transform[4];
        [SerializeField, Tooltip("World-space status line above the screen (ROS, bridge, mission).")] TMP_Text _status;
        [SerializeField, Tooltip("World-space text over the screen while there is no recent image.")] TMP_Text _placeholder;
        [SerializeField, Tooltip("Toolbar canvas (pivot at its top centre), child of the pivot.")] RectTransform _toolbar;
        [SerializeField] StatusChip _teleopChip;
        [SerializeField] StatusChip _missionChip;
        [SerializeField, Tooltip("Commanded speeds, or the controls hint while idle.")] TMP_Text _commandLabel;
        [SerializeField] Button _resumeButton;
        [SerializeField] Button _closeButton;
        [SerializeField, Tooltip("Active only while the panel is open (frame grab interactions, toolbar ray canvas).")]
        GameObject[] _activeWhileOpen = Array.Empty<GameObject>();

        [Header("Layout")]
        [SerializeField, Tooltip("Screen width (m); the height follows the 4:3 image.")] float _screenWidth = 0.64f;
        [SerializeField] float _aspect = 4f / 3f;
        [SerializeField] float _frameWidth = 0.02f;
        [SerializeField, Tooltip("Gap between the bottom frame bar and the toolbar (m).")] float _toolbarGap = 0.02f;
        [SerializeField, Tooltip("The panel (with status and toolbar) fits in this horizontal view angle (deg).")]
        float _fitHorizontalFov = 50f;
        [SerializeField, Tooltip("The panel (with status and toolbar) fits in this vertical view angle (deg).")]
        float _fitVerticalFov = 40f;
        [SerializeField] float _minOpenDistance = 0.5f;
        [SerializeField, Tooltip("The panel's visual centre sits this far below the eyes when it opens (m).")]
        float _openBelowEyes = 0.05f;

        [Header("Image")]
        [SerializeField, Tooltip("Without a new frame for this long (s) the placeholder replaces the image.")]
        float _staleAfter = 1.5f;

        public bool IsOpen { get; private set; }
        public event Action<bool> OpenChanged;

        Texture2D _texture;
        Material _screenMat;
        float _decodedFrameTime = float.NegativeInfinity;
        bool _hasImage, _warnedDecode;

        void Awake()
        {
            if (_teleop == null) _teleop = FindFirstObjectByType<FirstPersonTeleopController>(FindObjectsInactive.Include);
            if (_closeButton != null) _closeButton.onClick.AddListener(() => { if (_teleop != null) _teleop.Close(); else Close(); });
            if (_resumeButton != null) _resumeButton.onClick.AddListener(() =>
            {
                var c = RosHmiClient.Instance;
                if (c != null) c.SendMissionCmd("RESUME");
            });

            _texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "RobotCamera", wrapMode = TextureWrapMode.Clamp };
            if (_screen != null && _screen.sharedMaterial != null)
            {
                _screenMat = new Material(_screen.sharedMaterial);
                _screen.sharedMaterial = _screenMat;
                SetTexture(_screenMat, _texture);
            }
            foreach (var c in GetComponentsInChildren<Collider>(true)) c.isTrigger = true; // see OperationsPanel
            // The layout is saved in the prefab (context menu "Apply Layout"), so Editor tweaks are kept at runtime.
            // The content stays active during the first frame so the frame bars' ray interactables run their Start.
            SetVisible(true);
        }

        void Start()
        {
            if (_teleop != null)
            {
                _teleop.ActiveChanged += OnTeleopChanged;
                if (_teleop.IsActive) Open();
            }
            StartCoroutine(CloseAfterFirstFrame());
        }

        System.Collections.IEnumerator CloseAfterFirstFrame()
        {
            yield return null;
            if (!IsOpen) SetVisible(false);
        }

        void OnDestroy()
        {
            if (_teleop != null) _teleop.ActiveChanged -= OnTeleopChanged;
            if (_texture != null) Destroy(_texture);
            if (_screenMat != null) Destroy(_screenMat);
        }

        void OnTeleopChanged(bool active)
        {
            if (active) Open(); else Close();
        }

        // ---- Open / close ----------------------------------------------------------------------------------------

        public void Open()
        {
            if (IsOpen) return;
            SetVisible(true);
            PlaceInFrontOfHead();
            IsOpen = true;
            Refresh();
            Debug.Log(LogPrefix + "Camera panel opened");
            OpenChanged?.Invoke(true);
        }

        public void Close()
        {
            if (!IsOpen) return;
            SetVisible(false);
            IsOpen = false;
            Debug.Log(LogPrefix + "Camera panel closed");
            OpenChanged?.Invoke(false);
        }

        void SetVisible(bool on)
        {
            if (_content != null) _content.gameObject.SetActive(on);
            if (_toolbar != null) _toolbar.gameObject.SetActive(on);
            foreach (var go in _activeWhileOpen)
                if (go != null) go.SetActive(on);
        }

        /// <summary>Upright, facing the head, at the distance where screen, status and toolbar fit in view.</summary>
        void PlaceInFrontOfHead()
        {
            if (_head == null)
            {
                var eye = GameObject.Find("CenterEyeAnchor");
                if (eye != null) _head = eye.transform;
                if (_head == null) return;
            }
            var f = Vector3.ProjectOnPlane(_head.forward, Vector3.up);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(_head.up, Vector3.up);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            f.Normalize();

            Bounds(out float top, out float bottom, out float halfWidth);
            float d = PanelMath.FitDistance(2f * halfWidth, top - bottom, _fitHorizontalFov, _fitVerticalFov, _minOpenDistance);
            var rot = Quaternion.LookRotation(f, Vector3.up);
            var visualCentre = _head.position + f * d + Vector3.down * _openBelowEyes;
            transform.SetPositionAndRotation(visualCentre - rot * new Vector3(0f, 0.5f * (top + bottom), 0f), rot);
        }

        /// <summary>Vertical extent (pivot z = up) and half width of screen + frame + status + toolbar.</summary>
        void Bounds(out float top, out float bottom, out float halfWidth)
        {
            float hx = 0.5f * _screenWidth + _frameWidth, hz = 0.5f * _screenWidth / _aspect + _frameWidth;
            top = hz + (_status != null ? 0.06f : 0f);
            bottom = -hz;
            halfWidth = hx;
            if (_toolbar != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_toolbar);
                bottom -= _toolbarGap + _toolbar.rect.height * _toolbar.localScale.y;
                halfWidth = Mathf.Max(hx, 0.5f * _toolbar.rect.width * _toolbar.localScale.x);
            }
        }

        // ---- Layout (also from the context menu in the Editor) ---------------------------------------------------

        /// <summary>Places screen, backdrop, frame bars, status, placeholder and toolbar around the screen size.</summary>
        [ContextMenu("Apply Layout")]
        public void ApplyLayout()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var objs = new System.Collections.Generic.List<UnityEngine.Object>();
                foreach (var t in GetComponentsInChildren<Transform>(true)) objs.Add(t);
                UnityEditor.Undo.RecordObjects(objs.ToArray(), "Apply Layout");
            }
#endif
            float hx = 0.5f * _screenWidth, hz = 0.5f * _screenWidth / Mathf.Max(0.1f, _aspect), w = _frameWidth;
            if (_pivot != null) _pivot.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            if (_content != null) { _content.localPosition = Vector3.zero; _content.localRotation = Quaternion.identity; }
            if (_screen != null)
            {
                var t = _screen.transform;
                t.localPosition = new Vector3(0f, 0.001f, 0f);
                t.localRotation = Quaternion.Euler(90f, 0f, 0f); // quad front (-Z) towards the viewer, texture up = +Z
                t.localScale = new Vector3(2f * hx, 2f * hz, 1f);
            }
            if (_backdrop != null)
            {
                _backdrop.localPosition = new Vector3(0f, -0.003f, 0f);
                _backdrop.localRotation = Quaternion.identity;
                _backdrop.localScale = new Vector3(2f * hx, 0.002f, 2f * hz);
            }
            if (_bars != null && _bars.Length >= 4 && _bars[0] != null && _bars[1] != null && _bars[2] != null && _bars[3] != null)
            {
                float sx = 2f * (hx + w);
                Place(_bars[0], new Vector3(0f, 0f, -hz - 0.5f * w), new Vector3(sx, FrameHeight, w));
                Place(_bars[1], new Vector3(0f, 0f, hz + 0.5f * w), new Vector3(sx, FrameHeight, w));
                Place(_bars[2], new Vector3(-hx - 0.5f * w, 0f, 0f), new Vector3(w, FrameHeight, 2f * hz));
                Place(_bars[3], new Vector3(hx + 0.5f * w, 0f, 0f), new Vector3(w, FrameHeight, 2f * hz));
            }
            if (_status != null)
            {
                _status.transform.localPosition = new Vector3(0f, 0.002f, hz + w + 0.03f);
                _status.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }
            if (_placeholder != null)
            {
                _placeholder.transform.localPosition = new Vector3(0f, 0.004f, 0f);
                _placeholder.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }
            if (_toolbar != null)
            {
                _toolbar.localPosition = new Vector3(0f, 0.002f, -hz - w - _toolbarGap);
                _toolbar.localRotation = Quaternion.Euler(90f, 0f, 0f); // canvas up = pivot +z, facing the viewer
                // Same width as the frame (the height follows its ContentSizeFitter).
                float s = Mathf.Max(1e-5f, _toolbar.localScale.x);
                _toolbar.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 2f * (hx + w) / s);
            }
        }

        static void Place(Transform t, Vector3 pos, Vector3 scale)
        {
            t.localPosition = pos;
            t.localRotation = Quaternion.identity;
            t.localScale = scale;
        }

        // ---- Per frame -------------------------------------------------------------------------------------------

        void Update()
        {
            if (!IsOpen) return;
            DecodeLatestFrame();
            Refresh();
        }

        /// <summary>Decodes only the newest frame (at most one per rendered frame); older ones are dropped.</summary>
        void DecodeLatestFrame()
        {
            var c = RosHmiClient.Instance;
            if (c == null || c.LastCameraFrame == null || c.LastCameraFrameTime <= _decodedFrameTime) return;
            _decodedFrameTime = c.LastCameraFrameTime;
            if (_texture.LoadImage(c.LastCameraFrame, false))
            {
                _hasImage = true;
                if (_screenMat != null) SetTexture(_screenMat, _texture);
            }
            else if (!_warnedDecode)
            {
                _warnedDecode = true;
                Debug.LogWarning(LogPrefix + "camera frame could not be decoded (" + c.LastCameraFrame.Length + " bytes)");
            }
        }

        void Refresh()
        {
            var c = RosHmiClient.Instance;
            bool connected = c != null && c.IsConnected;
            bool fresh = _hasImage && c != null && Time.time - c.LastCameraFrameTime <= _staleAfter;

            if (_screen != null && _screen.enabled != fresh) _screen.enabled = fresh;
            if (_placeholder != null)
            {
                bool show = !fresh;
                if (_placeholder.gameObject.activeSelf != show) _placeholder.gameObject.SetActive(show);
                if (show) SetText(_placeholder, !connected ? "ROS disconnected" : _hasImage ? "Camera image lost" : "Waiting for the camera image");
            }

            var m = c != null ? c.Mission : null;
            if (_status != null)
            {
                string bridge = connected && !string.IsNullOrEmpty(c.BridgeStatus) ? c.BridgeStatus : "unknown";
                string mission = m == null ? "-" : (m.state ?? "-") + (!string.IsNullOrEmpty(m.message) ? " — " + m.message : "");
                SetText(_status, $"ROS: {(connected ? "connected" : "disconnected")} | Bridge: {bridge}\nMission: {mission}");
            }

            bool driving = _teleop != null && _teleop.IsDriving;
            if (_teleopChip != null)
            {
                if (!connected) _teleopChip.Set("Disconnected", StatusChip.Level.Error);
                else if (driving) _teleopChip.Set("Driving", StatusChip.Level.Ok);
                else if (!fresh) _teleopChip.Set("No camera", StatusChip.Level.Warn);
                else _teleopChip.Set("Idle", StatusChip.Level.Neutral);
            }
            if (_missionChip != null)
            {
                if (m == null || string.IsNullOrEmpty(m.state)) _missionChip.Set("-", StatusChip.Level.Neutral);
                else _missionChip.Set(VrMissionPage.TitleCase(m.state), VrMissionPage.MissionLevel(m.state));
            }
            if (_commandLabel != null)
                SetText(_commandLabel, driving
                    ? char.ToUpperInvariant(_teleop.DescribeCommand()[0]) + _teleop.DescribeCommand().Substring(1)
                    : "Left stick: drive · Right stick: turn");
            if (_resumeButton != null) _resumeButton.interactable = connected && m != null && m.IsPaused;
        }

        static void SetText(TMP_Text t, string s)
        {
            if (t.text != s) t.text = s;
        }

        static void SetTexture(Material m, Texture t)
        {
            m.SetTexture("_BaseMap", t);
            m.mainTexture = t;
        }
    }
}
