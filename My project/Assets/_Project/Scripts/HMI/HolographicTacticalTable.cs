using System;
using System.Globalization;
using RosMessageTypes.Nav;
using TMPro;
using UnityEngine;
using VRRobot.Core;
using VRRobot.SimWorld;

namespace VRRobot.HMI
{
    /// <summary>
    /// Holographic tactical table: a true miniature of the world (table-local = (world - center) * k, Unity axes
    /// preserved) showing the map, keepout mask, robot avatar, plan, goal and tool previews. The root stays active so it
    /// keeps receiving ROS events; only the <c>_content</c> child is toggled.
    /// </summary>
    public class HolographicTacticalTable : MonoBehaviour
    {
        const string LogPrefix = "[HMI] ";
        const float FrameHeight = 0.02f;
        const float SurfaceThickness = 0.005f;

        [SerializeField, Tooltip("CenterEyeAnchor.")] Transform _head;
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
        [SerializeField] float _tableSize = 1.0f;
        [SerializeField, Tooltip("Horizontal gap between the eyes and the near edge of the table when it opens.")]
        float _openNearGap = 0.3f;
        [SerializeField] float _openBelowEyes = 0.45f;
        [SerializeField] float _frameWidth = 0.03f;
        [SerializeField] TacticalToolController _tools;
        [SerializeField, Tooltip("Active only while the table is open (the Meta distance grab interaction), so a " +
                                 "closed table cannot be grabbed.")]
        GameObject[] _activeWhileOpen = Array.Empty<GameObject>();

        public bool IsOpen { get; private set; }
        public event Action<bool> OpenChanged;

        GridInfo _mapInfo, _keepInfo;
        bool _hasMap;
        Vector3 _center;
        float _k = 1f;
        float _rootDepth = 0.6f;

        OccupancyGridTexture _mapTex, _keepTex;
        Material _mapMat, _keepMat;
        Mesh _surfaceMesh, _keepMesh;
        Transform _surface, _backdrop, _keepout, _avatar;
        BoxCollider _surfaceBox;
        readonly Transform[] _bars = new Transform[4]; // near, far, left, right
        TextMeshPro _status;
        ToolFeedbackView _feedback;
        Transform _robotFallback;
        bool _subscribed, _warnedBadGrid;

        void Awake()
        {
            if (_content == null)
            {
                var c = new GameObject("Content");
                c.transform.SetParent(transform, false);
                _content = c.transform;
            }
            BuildVisuals();
            // The content stays active during the first frame: Meta's DistanceGrabInteractable caches the colliders
            // under its Rigidbody in Start (inactive ones are skipped), so the frame bars must exist and be active
            // then. The table closes right after (CloseAfterFirstFrame). The map surface is still inactive at that
            // point (no map yet), so only the frame bars become grab colliders.
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
            if (_head != null)
            {
                var f = _head.forward;
                f.y = 0f;
                if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(_head.up, Vector3.up);
                if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
                f.Normalize();
                var pos = _head.position + f * (_openNearGap + _rootDepth * 0.5f);
                pos.y = _head.position.y - _openBelowEyes;
                transform.SetPositionAndRotation(pos, Quaternion.LookRotation(f, Vector3.up));
            }
            transform.localScale = Vector3.one;
            _content.gameObject.SetActive(true);
            SetGrabActive(true);
            IsOpen = true;
            UpdateAvatar();
            RefreshFeedback();
            Debug.Log(LogPrefix + "Table opened");
            OpenChanged?.Invoke(true);
        }

        public void Close()
        {
            if (!IsOpen) return;
            _content.gameObject.SetActive(false);
            SetGrabActive(false);
            IsOpen = false;
            Debug.Log(LogPrefix + "Table closed");
            OpenChanged?.Invoke(false);
        }

        public void Toggle()
        {
            if (IsOpen) Close(); else Open();
        }

        // ---- Coordinates -----------------------------------------------------------------------------------------

        /// <summary>Converts a world point on the table surface to ROS map coordinates.</summary>
        public bool TryLocalToMap(Vector3 worldPoint, out Vector2 mapXY)
        {
            mapXY = default;
            if (!_hasMap) return false;
            var local = _content.InverseTransformPoint(worldPoint);
            mapXY = MapGridMath.WorldToMap(MapGridMath.TableLocalToWorld(local, _center, _k));
            return true;
        }

        public Vector3 MapToTableWorld(Vector2 mapXY, float localHeight)
        {
            var local = WorldToLocal(MapGridMath.MapToWorld(mapXY));
            local.y = localHeight;
            return _content.TransformPoint(local);
        }

        Vector3 WorldToLocal(Vector3 w) => MapGridMath.WorldToTableLocal(w, _center, _k);

        Vector3 MapToLocal(Vector2 xy, float h)
        {
            var l = WorldToLocal(MapGridMath.MapToWorld(xy));
            l.y = h;
            return l;
        }

        // ---- Visuals ---------------------------------------------------------------------------------------------

        void BuildVisuals()
        {
            _mapMat = _mapMaterial != null ? new Material(_mapMaterial) : null;
            _keepMat = _keepoutMaterial != null ? new Material(_keepoutMaterial) : null;
            _mapTex = new OccupancyGridTexture(new Color32(0, 0, 0, 0), new Color32(90, 200, 230, 210), new Color32(255, 255, 255, 255));
            _keepTex = OccupancyGridTexture.CreateMask(new Color32(255, 40, 40, 150), new Color32(0, 0, 0, 0));

            // Surface: own kinematic body so only the frame is part of the grab body's compound collider.
            _surfaceMesh = new Mesh { name = "TableSurface" };
            var sGo = new GameObject("Surface");
            sGo.transform.SetParent(_content, false);
            _surface = sGo.transform;
            sGo.AddComponent<MeshFilter>().sharedMesh = _surfaceMesh;
            var sr = sGo.AddComponent<MeshRenderer>();
            sr.sharedMaterial = _mapMat;
            sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _surfaceBox = sGo.AddComponent<BoxCollider>();
            var rb = sGo.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            sGo.AddComponent<TableSurface>().Table = this;
            sGo.SetActive(false);

            _backdrop = MakePrimitive(PrimitiveType.Cube, "Backdrop", _content, _backdropMaterial, false);
            _backdrop.gameObject.SetActive(false);

            _keepMesh = new Mesh { name = "TableKeepout" };
            var kGo = new GameObject("Keepout");
            kGo.transform.SetParent(_content, false);
            _keepout = kGo.transform;
            kGo.AddComponent<MeshFilter>().sharedMesh = _keepMesh;
            var kr = kGo.AddComponent<MeshRenderer>();
            kr.sharedMaterial = _keepMat;
            kr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            kGo.SetActive(false);

            var frame = new GameObject("Frame").transform;
            frame.SetParent(_content, false);
            string[] names = { "Near", "Far", "Left", "Right" };
            for (int i = 0; i < 4; i++)
            {
                _bars[i] = MakePrimitive(PrimitiveType.Cube, names[i], frame, _frameMaterial, true);
                // Active from the start (tiny until the first map): Meta's DistanceGrabInteractable caches the
                // colliders under its Rigidbody once, in Start, skipping inactive ones.
                _bars[i].localScale = Vector3.one * 0.001f;
            }

            var avatar = new GameObject("Avatar").transform;
            avatar.SetParent(_content, false);
            _avatar = avatar;
            MakePrimitive(PrimitiveType.Cylinder, "Body", avatar, _avatarMaterial, false).localScale = new Vector3(1f, 0.004f, 1f);
            MakePrimitive(PrimitiveType.Cube, "Heading", avatar, _avatarMaterial, false);
            avatar.gameObject.SetActive(false);

            _feedback = new ToolFeedbackView(_content, MapToLocal, () => _k, 0.004f, 0.006f, 0.3f,
                _planMaterial, _goalMaterial, _previewMaterial, _keepoutPreviewMaterial);

            var tGo = new GameObject("Status");
            tGo.transform.SetParent(_content, false);
            _status = tGo.AddComponent<TextMeshPro>();
            _status.fontSize = 0.25f;
            _status.alignment = TextAlignmentOptions.Center;
            _status.textWrappingMode = TextWrappingModes.NoWrap;
            _status.color = Color.white;
            _status.rectTransform.sizeDelta = new Vector2(1.4f, 0.1f);
            tGo.transform.localRotation = Quaternion.Euler(50f, 0f, 0f);
            tGo.transform.localPosition = new Vector3(0f, 0.05f, _tableSize * 0.5f + 0.1f);
            UpdateStatus();

            // Triggers only: the player's CharacterController, the recovery guard and CapsuleClearance ignore
            // triggers, so the hologram never pushes or "traps" the player (a 3x table can enclose the capsule).
            // HmiPointer and Meta's distance grab still hit them.
            foreach (var c in _content.GetComponentsInChildren<Collider>(true)) c.isTrigger = true;
        }

        static Transform MakePrimitive(PrimitiveType type, string name, Transform parent, Material mat, bool keepCollider)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            if (!keepCollider) DestroyImmediate(go.GetComponent<Collider>());
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
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
            _k = _tableSize / Mathf.Max(size.x, size.y);

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
            // the content is turned 90 degrees so the long side runs across the user's view when the table opens.
            var contentRot = b.size.z > b.size.x ? Quaternion.Euler(0f, -90f, 0f) : Quaternion.identity;
            _content.localRotation = contentRot;
            var rootMin = contentRot * b.min;
            var rootMax = contentRot * b.max;
            float rootFarZ = Mathf.Max(rootMin.z, rootMax.z);
            float rootCenterX = (rootMin.x + rootMax.x) * 0.5f;
            _rootDepth = Mathf.Abs(rootMax.z - rootMin.z) + 2f * w;
            var inv = Quaternion.Inverse(contentRot);
            _status.transform.localPosition = inv * new Vector3(rootCenterX, 0.05f, rootFarZ + w + 0.07f);
            _status.transform.localRotation = inv * Quaternion.Euler(50f, 0f, 0f);

            RebuildKeepout();
            UpdateAvatar();
            UpdatePlan();
            RefreshFeedback();
            Debug.Log(LogPrefix + "Table map rebuilt: scale k=" + _k.ToString("F3", CultureInfo.InvariantCulture));
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

        void Update()
        {
            TrySubscribe();
            if (!IsOpen) return;
            var c = RosHmiClient.Instance;
            if (c == null || !c.HasRobotPose) UpdateAvatar();
        }
    }
}
