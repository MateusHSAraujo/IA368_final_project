using System;
using System.Globalization;
using RosMessageTypes.Nav;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VRRobot.Core;
using VRRobot.SimWorld;

namespace VRRobot.HMI
{
    /// <summary>
    /// Operations panel: a holographic miniature of the world (content-local = (world - center) * k, Unity axes
    /// preserved) showing the map, keepout mask, robot avatar, plan, goal and tool previews, with a toolbar and corner
    /// resize handles. It opens vertical in front of the head, like the menus, and stays fixed in the world; the frame
    /// grab moves it, yaws it and pitches it down to horizontal. The root stays active so it keeps receiving
    /// ROS events; only the <c>_content</c> child and the <c>_activeWhileOpen</c> objects are toggled.
    /// </summary>
    /// <remarks>
    /// The root is upright (its rotation is the grab's yaw and pitch, pitch 0 = vertical). The visuals hang under
    /// <c>_pivot</c>, rotated -90 degrees about X, so the miniature lies in the pivot's XZ plane (+Y towards the
    /// viewer, +Z up). The frame grab uses <see cref="PanelGrabTransformer"/> (yaw + pitch 0..90, no roll, no
    /// scale); the corner handles resize through <see cref="UniformGrabScaleTransformer"/>.
    /// </remarks>
    public class OperationsPanel : MonoBehaviour
    {
        const string LogPrefix = "[HMI] ";
        const float FrameHeight = 0.02f;
        const float SurfaceThickness = 0.005f;

        [SerializeField, Tooltip("CenterEyeAnchor.")] Transform _head;
        [SerializeField, Tooltip("Child of the root, rotated -90 degrees about X; parent of the content, toolbar and " +
                                 "handles.")] Transform _pivot;
        [SerializeField] Transform _content;
        [Header("Materials")]
        [SerializeField] Material _mapMaterial;
        [SerializeField] Material _keepoutMaterial;
        [SerializeField] Material _frameMaterial;
        [SerializeField] Material _avatarMaterial;
        [SerializeField] Material _planMaterial;
        [SerializeField] Material _goalMaterial;
        [SerializeField] Material _previewMaterial;
        [SerializeField] Material _keepoutPreviewMaterial;
        [SerializeField] Material _backdropMaterial;
        [Header("Layout")]
        [SerializeField, Tooltip("Longest map side at scale 1 (m).")]
        float _panelSize = 0.5f;
        [SerializeField] float _frameWidth = 0.02f;
        [SerializeField, Tooltip("Gap between the near frame bar and the toolbar (m); clears the near handles.")]
        float _toolbarGap = 0.045f;
        [SerializeField, Tooltip("World size of the corner handles (constant while the panel scales).")]
        float _handleSize = 0.035f;

        [Header("Open placement")]
        [SerializeField, Tooltip("The panel (with toolbar and status) fits in this horizontal view angle (deg).")]
        float _fitHorizontalFov = 55f;
        [SerializeField, Tooltip("The panel (with toolbar and status) fits in this vertical view angle (deg).")]
        float _fitVerticalFov = 45f;
        [SerializeField] float _minOpenDistance = 0.45f;
        [SerializeField, Tooltip("The panel's centre sits this far below the eyes when it opens (m).")]
        float _openBelowEyes = 0.1f;

        [Header("Visuals (prefab objects under Content; laid out by Apply Default Layout, then by each /hmi/map)")]
        [SerializeField, Tooltip("Map surface: MeshFilter (mesh built per map), trigger BoxCollider, kinematic " +
                                 "Rigidbody, PanelSurface.")] MeshFilter _surfaceFilter;
        [SerializeField] BoxCollider _surfaceBox;
        [SerializeField, Tooltip("Dark plate behind the map; also what an empty (no map yet) panel shows.")]
        Transform _backdrop;
        [SerializeField, Tooltip("Keepout overlay (mesh built per mask).")] MeshFilter _keepoutFilter;
        [SerializeField, Tooltip("Frame bars near, far, left, right: trigger BoxCollider + ColliderSurface + " +
                                 "RayInteractable (frame grab).")] Transform[] _bars = new Transform[4];
        [SerializeField, Tooltip("Robot avatar: children Body (cylinder) and Heading (cube).")] Transform _avatar;
        [SerializeField] TextMeshPro _status;

        [Header("Parts")]
        [SerializeField, Tooltip("Toolbar canvas (pivot at its top centre), child of the pivot.")] RectTransform _toolbar;
        [SerializeField, Tooltip("Corner handles, children of the pivot: near-left, near-right, far-left, far-right.")]
        Transform[] _handles = Array.Empty<Transform>();
        [SerializeField] Button _closeButton;
        [SerializeField] TacticalToolController _tools;
        [SerializeField, Tooltip("Active only while the panel is open (grab interactions, toolbar, handles), so a " +
                                 "closed panel cannot be grabbed.")]
        GameObject[] _activeWhileOpen = Array.Empty<GameObject>();

        [Header("Input")]
        [SerializeField, Tooltip("Opens and closes the panel (Y on the left controller).")]
        OVRInput.Button _toggleButton = OVRInput.Button.Four;

        public bool IsOpen { get; private set; }
        public event Action<bool> OpenChanged;

        GridInfo _mapInfo, _keepInfo;
        bool _hasMap;
        Vector3 _center;
        float _k = 1f;
        // Pivot-space XZ bounds of the map including the frame (x across, z = up when the panel is vertical).
        Vector2 _frameMin = new Vector2(-0.25f, -0.15f), _frameMax = new Vector2(0.25f, 0.15f);

        OccupancyGridTexture _mapTex, _keepTex;
        Material _mapMat, _keepMat;
        Mesh _surfaceMesh, _keepMesh;
        Transform _surface, _keepout;
        ToolFeedbackView _feedback;
        Transform _robotFallback;
        bool _subscribed, _warnedBadGrid;

        void Awake()
        {
            if (_pivot == null)
            {
                var p = new GameObject("Pivot");
                p.transform.SetParent(transform, false);
                p.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
                _pivot = p.transform;
            }
            if (_content == null)
            {
                var c = new GameObject("Content");
                c.transform.SetParent(_pivot, false);
                _content = c.transform;
            }
            if (_closeButton != null) _closeButton.onClick.AddListener(Close);
            BuildVisuals();
            ApplyDefaultLayout(); // until the first map: the empty panel must be grabbable and its toolbar usable
            // The content stays active during the first frame so the frame bars' ray interactables run their Start;
            // the panel closes right after (CloseAfterFirstFrame).
            _content.gameObject.SetActive(true);
        }

        System.Collections.IEnumerator CloseAfterFirstFrame()
        {
            yield return null;
            if (IsOpen) yield break;
            _content.gameObject.SetActive(false);
            SetGrabActive(false);
        }

        void SetGrabActive(bool on)
        {
            foreach (var go in _activeWhileOpen)
                if (go != null) go.SetActive(on);
        }

        void OnEnable()
        {
            TrySubscribe();
            if (_tools != null) _tools.PreviewChanged += RefreshFeedback;
        }

        void Start()
        {
            TrySubscribe();
            StartCoroutine(CloseAfterFirstFrame());
        }

        void OnDisable()
        {
            if (_tools != null) _tools.PreviewChanged -= RefreshFeedback;
            var c = RosHmiClient.Instance;
            if (_subscribed && c != null)
            {
                c.MapReceived -= OnMap;
                c.KeepoutMaskReceived -= OnKeepout;
                c.RobotPoseReceived -= OnRobotPose;
                c.PlanReceived -= OnPlan;
                c.MissionStateChanged -= OnMission;
                c.ConnectionChanged -= OnConnection;
                c.BridgeStatusChanged -= OnBridge;
            }
            _subscribed = false;
        }

        void OnDestroy()
        {
            _mapTex?.Dispose();
            _keepTex?.Dispose();
            if (_mapMat != null) Destroy(_mapMat);
            if (_keepMat != null) Destroy(_keepMat);
            if (_surfaceMesh != null) Destroy(_surfaceMesh);
            if (_keepMesh != null) Destroy(_keepMesh);
        }

        void TrySubscribe()
        {
            var c = RosHmiClient.Instance;
            if (_subscribed || c == null) return;
            c.MapReceived += OnMap;
            c.KeepoutMaskReceived += OnKeepout;
            c.RobotPoseReceived += OnRobotPose;
            c.PlanReceived += OnPlan;
            c.MissionStateChanged += OnMission;
            c.ConnectionChanged += OnConnection;
            c.BridgeStatusChanged += OnBridge;
            _subscribed = true;

            if (c.LastMap != null) OnMap(c.LastMap, c.LastMapInfo);
            if (c.LastKeepoutMask != null) OnKeepout(c.LastKeepoutMask, c.LastKeepoutInfo);
            UpdateAvatar();
            UpdatePlan();
            UpdateStatus();
        }

        // ---- Open / close ----------------------------------------------------------------------------------------

        public void Open()
        {
            if (IsOpen) return;
            transform.localScale = Vector3.one;
            _content.gameObject.SetActive(true);
            SetGrabActive(true);
            PlaceInFrontOfHead();
            IsOpen = true;
            UpdateAvatar();
            RefreshFeedback();
            Debug.Log(LogPrefix + "Panel opened");
            OpenChanged?.Invoke(true);
        }

        public void Close()
        {
            if (!IsOpen) return;
            _content.gameObject.SetActive(false);
            SetGrabActive(false);
            IsOpen = false;
            Debug.Log(LogPrefix + "Panel closed");
            OpenChanged?.Invoke(false);
        }

        public void Toggle()
        {
            if (IsOpen) Close(); else Open();
        }

        /// <summary>
        /// Upright, facing the head, at the distance where the whole panel (frame, status line and toolbar) fits in
        /// the configured view angles, with its visual centre slightly below the eyes.
        /// </summary>
        void PlaceInFrontOfHead()
        {
            if (_head == null) return;
            var f = Vector3.ProjectOnPlane(_head.forward, Vector3.up);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(_head.up, Vector3.up);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            f.Normalize();

            // Vertical extent in pivot z (= up): status line above the far bar, toolbar below the near bar.
            float top = _frameMax.y + (_status != null ? 0.06f : 0f);
            float bottom = _frameMin.y - ToolbarHeight();
            float width = Mathf.Max(_frameMax.x - _frameMin.x, ToolbarWidth()) + 2f * _handleSize;
            float height = top - bottom + 2f * _handleSize;
            float d = PanelMath.FitDistance(width, height, _fitHorizontalFov, _fitVerticalFov, _minOpenDistance);

            float centreUp = 0.5f * (top + bottom);
            float centreRight = 0.5f * (_frameMin.x + _frameMax.x);
            var rot = Quaternion.LookRotation(f, Vector3.up);
            var visualCentre = _head.position + f * d + Vector3.down * _openBelowEyes;
            // Pivot x = root right, pivot z = root up.
            transform.SetPositionAndRotation(visualCentre - rot * new Vector3(centreRight, centreUp, 0f), rot);
        }

        float ToolbarHeight()
        {
            if (_toolbar == null || !_toolbar.gameObject.activeInHierarchy) return 0f;
            LayoutRebuilder.ForceRebuildLayoutImmediate(_toolbar);
            return _toolbarGap + _toolbar.rect.height * _toolbar.localScale.y;
        }

        float ToolbarWidth() => _toolbar != null ? _toolbar.rect.width * _toolbar.localScale.x : 0f;

        // ---- Coordinates -----------------------------------------------------------------------------------------

        /// <summary>Converts a world point on the panel surface to ROS map coordinates.</summary>
        public bool TryLocalToMap(Vector3 worldPoint, out Vector2 mapXY)
        {
            mapXY = default;
            if (!_hasMap) return false;
            var local = _content.InverseTransformPoint(worldPoint);
            mapXY = MapGridMath.WorldToMap(MapGridMath.PanelLocalToWorld(local, _center, _k));
            return true;
        }

        public Vector3 MapToPanelWorld(Vector2 mapXY, float localHeight)
        {
            var local = WorldToLocal(MapGridMath.MapToWorld(mapXY));
            local.y = localHeight;
            return _content.TransformPoint(local);
        }

        Vector3 WorldToLocal(Vector3 w) => MapGridMath.WorldToPanelLocal(w, _center, _k);

        Vector3 MapToLocal(Vector2 xy, float h)
        {
            var l = WorldToLocal(MapGridMath.MapToWorld(xy));
            l.y = h;
            return l;
        }

        // ---- Visuals ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Runtime setup of the prefab visuals: per-instance materials and textures, the meshes rebuilt per map, and
        /// the initial visibility (no map yet: backdrop and frame only).
        /// </summary>
        void BuildVisuals()
        {
            _mapMat = _mapMaterial != null ? new Material(_mapMaterial) : null;
            _keepMat = _keepoutMaterial != null ? new Material(_keepoutMaterial) : null;
            _mapTex = new OccupancyGridTexture(new Color32(0, 0, 0, 0), new Color32(90, 200, 230, 210), new Color32(255, 255, 255, 255));
            _keepTex = OccupancyGridTexture.CreateMask(new Color32(255, 40, 40, 150), new Color32(0, 0, 0, 0));

            if (_surfaceFilter == null || _surfaceBox == null || _keepoutFilter == null || _backdrop == null ||
                _avatar == null || _status == null || _bars.Length < 4)
            {
                Debug.LogError(LogPrefix + "OperationsPanel: visual references are missing in the prefab", this);
                enabled = false;
                return;
            }

            _surface = _surfaceFilter.transform;
            _surfaceMesh = new Mesh { name = "PanelSurface" };
            _surfaceFilter.sharedMesh = _surfaceMesh;
            var sr = _surfaceFilter.GetComponent<MeshRenderer>();
            if (sr != null && _mapMat != null) sr.sharedMaterial = _mapMat;
            var ps = _surfaceFilter.GetComponent<PanelSurface>();
            if (ps != null) ps.Panel = this;
            _surface.gameObject.SetActive(false); // until the first map

            _keepout = _keepoutFilter.transform;
            _keepMesh = new Mesh { name = "PanelKeepout" };
            _keepoutFilter.sharedMesh = _keepMesh;
            var kr = _keepoutFilter.GetComponent<MeshRenderer>();
            if (kr != null && _keepMat != null) kr.sharedMaterial = _keepMat;
            _keepout.gameObject.SetActive(false);

            _avatar.gameObject.SetActive(false); // until a robot pose

            _feedback = new ToolFeedbackView(_content, MapToLocal, () => _k, 0.004f, 0.006f, 0.3f,
                _planMaterial, _goalMaterial, _previewMaterial, _keepoutPreviewMaterial);
            UpdateStatus();

            // Triggers only: the player's CharacterController, the recovery guard and CapsuleClearance ignore
            // triggers, so the hologram never pushes or "traps" the player (a scaled-up panel can enclose the capsule).
            // HmiPointer's raycast and the ISDK collider surfaces still hit them.
            foreach (var c in _content.GetComponentsInChildren<Collider>(true)) c.isTrigger = true;
        }

        /// <summary>
        /// Lays out the frame, backdrop, surface collider, status line, toolbar and handles around the default bounds
        /// (<c>_panelSize</c> wide, 0.64 of it tall). Runs in <c>Awake</c> (so an empty panel can be grabbed and used
        /// before the first map) and from the component's context menu in the Editor. Each map re-lays them out.
        /// </summary>
        [ContextMenu("Apply Default Layout")]
        public void ApplyDefaultLayout()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var objs = new System.Collections.Generic.List<UnityEngine.Object>();
                foreach (var t in GetComponentsInChildren<Transform>(true)) objs.Add(t);
                if (_surfaceBox != null) objs.Add(_surfaceBox);
                UnityEditor.Undo.RecordObjects(objs.ToArray(), "Apply Default Layout");
            }
#endif
            float hx = 0.5f * _panelSize, hz = 0.32f * _panelSize, w = _frameWidth;
            _frameMin = new Vector2(-hx - w, -hz - w);
            _frameMax = new Vector2(hx + w, hz + w);
            if (_content != null) _content.localRotation = Quaternion.identity;

            float sx = _frameMax.x - _frameMin.x, sz = _frameMax.y - _frameMin.y;
            if (_bars.Length >= 4 && _bars[0] != null && _bars[1] != null && _bars[2] != null && _bars[3] != null)
            {
                Place(_bars[0], new Vector3(0f, 0f, _frameMin.y + w * 0.5f), new Vector3(sx, FrameHeight, w));
                Place(_bars[1], new Vector3(0f, 0f, _frameMax.y - w * 0.5f), new Vector3(sx, FrameHeight, w));
                Place(_bars[2], new Vector3(_frameMin.x + w * 0.5f, 0f, 0f), new Vector3(w, FrameHeight, sz - 2f * w));
                Place(_bars[3], new Vector3(_frameMax.x - w * 0.5f, 0f, 0f), new Vector3(w, FrameHeight, sz - 2f * w));
            }
            if (_backdrop != null)
            {
                _backdrop.localPosition = new Vector3(0f, -0.003f, 0f);
                _backdrop.localScale = new Vector3(2f * hx, 0.002f, 2f * hz);
            }
            if (_surfaceBox != null)
            {
                _surfaceBox.center = new Vector3(0f, -SurfaceThickness * 0.5f, 0f);
                _surfaceBox.size = new Vector3(2f * hx, SurfaceThickness, 2f * hz);
            }
            if (_avatar != null) _avatar.localPosition = new Vector3(0f, 0.004f, 0f);
            if (_status != null)
            {
                _status.transform.localPosition = new Vector3(0f, 0.002f, _frameMax.y + 0.03f);
                _status.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }
            LayoutParts();
        }

        static void SetTexture(Material m, Texture t)
        {
            if (m == null) return;
            m.SetTexture("_BaseMap", t);
            m.mainTexture = t;
        }

        // ---- ROS events ------------------------------------------------------------------------------------------

        void OnMap(OccupancyGridMsg msg, GridInfo info)
        {
            if (!info.IsValid || msg.data == null || msg.data.Length != info.Width * info.Height)
            {
                WarnBadGrid("/map");
                return;
            }
            _mapTex.Apply(msg.data, info);
            SetTexture(_mapMat, _mapTex.Texture);
            if (!_hasMap || !Same(_mapInfo, info))
            {
                _mapInfo = info;
                _hasMap = true;
                RebuildGeometry();
            }
        }

        void OnKeepout(OccupancyGridMsg msg, GridInfo info)
        {
            if (!info.IsValid || msg.data == null || msg.data.Length != info.Width * info.Height)
            {
                WarnBadGrid("keepout mask");
                return;
            }
            _keepTex.Apply(msg.data, info);
            SetTexture(_keepMat, _keepTex.Texture);
            if (!Same(_keepInfo, info))
            {
                _keepInfo = info;
                RebuildKeepout();
            }
        }

        void WarnBadGrid(string what)
        {
            if (_warnedBadGrid) return;
            _warnedBadGrid = true;
            Debug.LogWarning(LogPrefix + what + " ignored: invalid size or data length");
        }

        void OnRobotPose(Vector3 p, Quaternion r) => UpdateAvatar();

        void OnPlan(System.Collections.Generic.IReadOnlyList<Vector3> pts) => UpdatePlan();

        void OnMission(VRRobot.HMI.MissionState s)
        {
            UpdatePlan();
            UpdateStatus();
        }

        void OnConnection(bool c) => UpdateStatus();

        void OnBridge(string s) => UpdateStatus();

        static bool Same(GridInfo a, GridInfo b) =>
            a.Width == b.Width && a.Height == b.Height && Mathf.Approximately(a.Resolution, b.Resolution) && a.Origin == b.Origin;

        void RebuildGeometry()
        {
            _center = MapGridMath.WorldCenter(_mapInfo);
            var size = MapGridMath.WorldSizeXZ(_mapInfo);
            _k = _panelSize / Mathf.Max(size.x, size.y);

            GridQuadMesh.Build(_surfaceMesh, _mapInfo, WorldToLocal, 0f);
            var b = _surfaceMesh.bounds;
            _surface.gameObject.SetActive(true);
            _surfaceBox.center = new Vector3(b.center.x, -SurfaceThickness * 0.5f, b.center.z);
            _surfaceBox.size = new Vector3(b.size.x, SurfaceThickness, b.size.z);

            _backdrop.gameObject.SetActive(true);
            _backdrop.localPosition = new Vector3(b.center.x, -0.003f, b.center.z);
            _backdrop.localScale = new Vector3(b.size.x, 0.002f, b.size.z);

            float w = _frameWidth;
            float sx = b.size.x, sz = b.size.z, cx = b.center.x, cz = b.center.z;
            Place(_bars[0], new Vector3(cx, 0f, b.min.z - w * 0.5f), new Vector3(sx + 2f * w, FrameHeight, w));
            Place(_bars[1], new Vector3(cx, 0f, b.max.z + w * 0.5f), new Vector3(sx + 2f * w, FrameHeight, w));
            Place(_bars[2], new Vector3(b.min.x - w * 0.5f, 0f, cz), new Vector3(w, FrameHeight, sz));
            Place(_bars[3], new Vector3(b.max.x + w * 0.5f, 0f, cz), new Vector3(w, FrameHeight, sz));

            // The map content is laid out in world orientation (a true miniature); when the map is deeper than wide,
            // the content is turned 90 degrees so the long side runs across the panel.
            var contentRot = b.size.z > b.size.x ? Quaternion.Euler(0f, -90f, 0f) : Quaternion.identity;
            _content.localRotation = contentRot;
            var a = contentRot * b.min;
            var c = contentRot * b.max;
            _frameMin = new Vector2(Mathf.Min(a.x, c.x) - w, Mathf.Min(a.z, c.z) - w);
            _frameMax = new Vector2(Mathf.Max(a.x, c.x) + w, Mathf.Max(a.z, c.z) + w);
            float centerX = 0.5f * (_frameMin.x + _frameMax.x);

            // Status line above the far bar, lying in the panel plane (readable from the surface side).
            var inv = Quaternion.Inverse(contentRot);
            _status.transform.localPosition = inv * new Vector3(centerX, 0.002f, _frameMax.y + 0.03f);
            _status.transform.localRotation = inv * Quaternion.Euler(90f, 0f, 0f);
            LayoutParts();

            RebuildKeepout();
            UpdateAvatar();
            UpdatePlan();
            RefreshFeedback();
            Debug.Log(LogPrefix + "Panel map rebuilt: scale k=" + _k.ToString("F3", CultureInfo.InvariantCulture));
        }

        /// <summary>Toolbar under the near bar, handles on the frame corners (pivot space).</summary>
        void LayoutParts()
        {
            float centerX = 0.5f * (_frameMin.x + _frameMax.x);
            if (_toolbar != null)
            {
                _toolbar.localPosition = new Vector3(centerX, 0.002f, _frameMin.y - _toolbarGap);
                _toolbar.localRotation = Quaternion.Euler(90f, 0f, 0f); // canvas up = pivot +z, facing the viewer
            }
            for (int i = 0; i < _handles.Length && i < 4; i++)
            {
                if (_handles[i] == null) continue;
                PlaceHandle(i, Mathf.Max(1e-3f, transform.localScale.x));
            }
        }

        /// <summary>
        /// Handle <paramref name="i"/> just outside its frame corner, diagonally, so the ray hits the handle rather
        /// than the frame bar. The offset is a constant world distance (divided by the root scale), like the size.
        /// </summary>
        void PlaceHandle(int i, float rootScale)
        {
            float sx = (i % 2 == 0) ? -1f : 1f, sz = (i < 2) ? -1f : 1f;
            float offset = _handleSize * 0.7f / rootScale;
            float x = (sx < 0f ? _frameMin.x : _frameMax.x) + sx * offset;
            float z = (sz < 0f ? _frameMin.y : _frameMax.y) + sz * offset;
            _handles[i].localPosition = new Vector3(x, 0f, z);
        }

        static void Place(Transform t, Vector3 pos, Vector3 scale)
        {
            t.gameObject.SetActive(true);
            t.localPosition = pos;
            t.localScale = scale;
        }

        void RebuildKeepout()
        {
            if (!_hasMap || !_keepInfo.IsValid) return;
            GridQuadMesh.Build(_keepMesh, _keepInfo, WorldToLocal, 0.001f / _k);
            _keepout.gameObject.SetActive(true);
        }

        void UpdateAvatar()
        {
            if (_avatar == null) return;
            var c = RosHmiClient.Instance;
            Vector3 pos;
            Quaternion rot;
            if (c != null && c.HasRobotPose) { pos = c.RobotPosition; rot = c.RobotRotation; }
            else
            {
                if (_robotFallback == null && SimWorldRegistry.Instance != null)
                    _robotFallback = SimWorldRegistry.Instance.Find("/myRobot");
                if (_robotFallback == null || !_hasMap) { _avatar.gameObject.SetActive(false); return; }
                pos = _robotFallback.position;
                rot = _robotFallback.rotation;
            }
            if (!_hasMap) { _avatar.gameObject.SetActive(false); return; }

            float d = Mathf.Max(0.45f * _k, 0.02f);
            var body = _avatar.GetChild(0);
            var arrow = _avatar.GetChild(1);
            body.localScale = new Vector3(d, 0.004f, d);
            arrow.localScale = new Vector3(d * 0.14f, 0.004f, d * 0.9f);
            arrow.localPosition = new Vector3(0f, 0.005f, d * 0.4f);

            var f = rot * Vector3.forward;
            float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            var local = WorldToLocal(pos);
            local.y = 0.004f;
            _avatar.localPosition = local;
            _avatar.localRotation = Quaternion.Euler(0f, yaw, 0f);
            _avatar.gameObject.SetActive(true);
        }

        void UpdatePlan()
        {
            if (_feedback == null || !_hasMap) return;
            var c = RosHmiClient.Instance;
            bool visible = c != null && c.Mission != null && c.Mission.IsNavigating;
            _feedback.SetPlan(c != null ? c.LastPlan : null, visible);
        }

        void RefreshFeedback()
        {
            if (_feedback != null && _hasMap) _feedback.Refresh(_tools);
        }

        void UpdateStatus()
        {
            if (_status == null) return;
            var c = RosHmiClient.Instance;
            bool connected = c != null && c.IsConnected;
            // The last bridge status is stale while ROS is disconnected.
            string bridge = connected && !string.IsNullOrEmpty(c.BridgeStatus) ? c.BridgeStatus : "unknown";
            string mission = "-";
            var m = c != null ? c.Mission : null;
            if (m != null)
            {
                mission = m.state ?? "-";
                if (m.IsActive) mission += $" ({m.mode ?? "-"}, {m.remaining} left)";
                if (!string.IsNullOrEmpty(m.message)) mission += " — " + m.message;
            }
            _status.text = $"ROS: {(connected ? "connected" : "disconnected")} | Bridge: {bridge}\nMission: {mission}";
        }

        void LateUpdate()
        {
            if (!IsOpen) return;
            // Handles keep a constant world size and corner offset while the panel scales (like Meta's ParentScaleInverter).
            float s = Mathf.Max(1e-3f, transform.localScale.x);
            for (int i = 0; i < _handles.Length && i < 4; i++)
            {
                if (_handles[i] == null) continue;
                _handles[i].localScale = Vector3.one * (_handleSize / s);
                PlaceHandle(i, s);
            }
        }

        void Update()
        {
            TrySubscribe();
            if (OVRInput.GetDown(_toggleButton)) Toggle();
            if (!IsOpen) return;
            var c = RosHmiClient.Instance;
            if (c == null || !c.HasRobotPose) UpdateAvatar();
        }
    }
}
