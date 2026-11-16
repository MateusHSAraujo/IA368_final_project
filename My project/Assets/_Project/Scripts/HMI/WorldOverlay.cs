using System.Collections.Generic;
using RosMessageTypes.Nav;
using UnityEngine;
using VRRobot.Core;

namespace VRRobot.HMI
{
    /// <summary>
    /// The tool feedback drawn in the real 3D world (map frame == world): plan, goal ring, active path, keepout
    /// overlay and stroke previews. Always visible, independent of the operations panel.
    /// </summary>
    public class WorldOverlay : MonoBehaviour
    {
        [SerializeField] TacticalToolController _tools;
        [SerializeField] Material _planMaterial;
        [SerializeField] Material _goalMaterial;
        [SerializeField] Material _previewMaterial;
        [SerializeField] Material _keepoutPreviewMaterial;
        [SerializeField] Material _keepoutMaterial;
        [SerializeField] float _height = 0.03f;
        [SerializeField] float _keepoutHeight = 0.02f;
        [SerializeField] float _lineWidth = 0.06f;
        [SerializeField] float _goalRadius = 0.3f;

        OccupancyGridTexture _keepTex;
        Material _keepMat;
        Mesh _keepMesh;
        GameObject _keepGo;
        GridInfo _keepInfo;
        ToolFeedbackView _feedback;
        bool _subscribed, _warnedBadGrid;

        void Awake()
        {
            // Map/world coordinates are used as local positions: the overlay must sit at the world origin.
            if (transform.parent != null && (transform.lossyScale != Vector3.one || transform.parent.position != Vector3.zero
                                             || transform.parent.rotation != Quaternion.identity))
                Debug.LogWarning("[HMI] WorldOverlay has a parent with a non-identity world matrix; forcing the world pose");
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            if (transform.parent == null) transform.localScale = Vector3.one;
            _feedback = new ToolFeedbackView(transform, (xy, h) => MapGridMath.MapToWorld(xy, h), () => 1f,
                _lineWidth, _height, _goalRadius, _planMaterial, _goalMaterial, _previewMaterial, _keepoutPreviewMaterial);

            _keepTex = OccupancyGridTexture.CreateMask(new Color32(255, 40, 40, 150), new Color32(0, 0, 0, 0));
            _keepMat = _keepoutMaterial != null ? new Material(_keepoutMaterial) : null;
            _keepMesh = new Mesh { name = "WorldKeepout" };
            _keepGo = new GameObject("Keepout");
            _keepGo.transform.SetParent(transform, false);
            _keepGo.AddComponent<MeshFilter>().sharedMesh = _keepMesh;
            var r = _keepGo.AddComponent<MeshRenderer>();
            r.sharedMaterial = _keepMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            _keepGo.SetActive(false);
        }

        void OnEnable()
        {
            TrySubscribe();
            if (_tools != null) _tools.PreviewChanged += Refresh;
        }

        void Start() => TrySubscribe();

        void Update() => TrySubscribe();

        void OnDisable()
        {
            if (_tools != null) _tools.PreviewChanged -= Refresh;
            var c = RosHmiClient.Instance;
            if (_subscribed && c != null)
            {
                c.PlanReceived -= OnPlan;
                c.MissionStateChanged -= OnMission;
                c.KeepoutMaskReceived -= OnKeepout;
            }
            _subscribed = false;
        }

        void OnDestroy()
        {
            _keepTex?.Dispose();
            if (_keepMat != null) Destroy(_keepMat);
            if (_keepMesh != null) Destroy(_keepMesh);
        }

        void TrySubscribe()
        {
            var c = RosHmiClient.Instance;
            if (_subscribed || c == null) return;
            c.PlanReceived += OnPlan;
            c.MissionStateChanged += OnMission;
            c.KeepoutMaskReceived += OnKeepout;
            _subscribed = true;
            if (c.LastKeepoutMask != null) OnKeepout(c.LastKeepoutMask, c.LastKeepoutInfo);
            UpdatePlan();
            Refresh();
        }

        void OnPlan(IReadOnlyList<Vector3> pts) => UpdatePlan();

        void OnMission(MissionState s) => UpdatePlan();

        void OnKeepout(OccupancyGridMsg msg, GridInfo info)
        {
            if (!info.IsValid || msg.data == null || msg.data.Length != info.Width * info.Height)
            {
                if (!_warnedBadGrid) Debug.LogWarning("[HMI] keepout mask ignored: invalid size or data length");
                _warnedBadGrid = true;
                return;
            }
            _keepTex.Apply(msg.data, info);
            if (_keepMat != null)
            {
                _keepMat.SetTexture("_BaseMap", _keepTex.Texture);
                _keepMat.mainTexture = _keepTex.Texture;
            }
            if (!_keepGo.activeSelf || _keepInfo.Width != info.Width || _keepInfo.Height != info.Height
                || !Mathf.Approximately(_keepInfo.Resolution, info.Resolution) || _keepInfo.Origin != info.Origin)
            {
                _keepInfo = info;
                GridQuadMesh.Build(_keepMesh, info, w => w, _keepoutHeight);
                _keepGo.SetActive(true);
            }
        }

        void UpdatePlan()
        {
            var c = RosHmiClient.Instance;
            bool visible = c != null && c.Mission != null && c.Mission.IsNavigating;
            _feedback.SetPlan(c != null ? c.LastPlan : null, visible);
        }

        void Refresh() => _feedback.Refresh(_tools);
    }
}
