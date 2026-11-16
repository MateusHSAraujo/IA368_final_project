using System;
using Oculus.Interaction;
using UnityEngine;
using VRRobot.Core;
using VRRobot.SimWorld;
using VRRobot.XR;

namespace VRRobot.HMI
{
    public enum HitKind { None, Panel, Floor, Blocked }

    /// <summary>
    /// Tool pointer on the right controller: casts the ray of the ISDK ray interactor against the physics scene and
    /// resolves it to a point on the operations panel or on the real floor, in map coordinates. Yields to the ISDK
    /// whenever the ray is hovering something it handles (menu canvas, panel frame grab).
    /// </summary>
    [DefaultExecutionOrder(-10)]
    public class HmiPointer : MonoBehaviour
    {
        [SerializeField, Tooltip("Right controller RayInteractor (Oculus.Interaction.RayInteractor).")] RayInteractor _rayInteractor;
        [SerializeField, Tooltip("The RayInteractor's ray origin transform. Read directly: while a higher-priority " +
                                 "interactor of the same group hovers (e.g. a distance grab), the " +
                                 "RayInteractor is Disabled and its Origin/Forward stop updating.")]
        Transform _rayOrigin;
        [SerializeField, Tooltip("Camera Rig root: colliders under it are skipped.")] Transform _ignoreRoot;
        [SerializeField] VrFlyMode _flyMode;
        [SerializeField] float _maxDistance = 15f;
        [SerializeField] float _floorMaxHeight = 0.15f;
        [SerializeField] float _floorMinNormalY = 0.7f;
        [SerializeField] float _pressThreshold = 0.55f;
        [SerializeField] float _releaseThreshold = 0.35f;
        [SerializeField] LineRenderer _line;
        [SerializeField] Transform _reticle;
        [SerializeField] Color _validColor = new Color(0.4f, 1f, 1f, 0.9f);
        [SerializeField] Color _invalidColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        [SerializeField, UnityEngine.Serialization.FormerlySerializedAs("_reticleSizeTable")] float _reticleSizePanel = 0.015f;
        [SerializeField] float _reticleSizeFloor = 0.12f;

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        public RayInteractor RayInteractor => _rayInteractor;

        /// <summary>Current controller ray, valid even while the RayInteractor is disabled by its group.</summary>
        public Ray CurrentRay => _rayOrigin != null
            ? new Ray(_rayOrigin.position, _rayOrigin.forward)
            : new Ray(_rayInteractor != null ? _rayInteractor.Origin : Vector3.zero,
                      _rayInteractor != null ? _rayInteractor.Forward : Vector3.forward);

        /// <summary>Set by the tool controller: the pointer does nothing while false.</summary>
        public bool Active { get; set; }

        /// <summary>Active and not flying (the triggers then move up/down).</summary>
        public bool Operative => Active && (_flyMode == null || !_flyMode.IsFlying);

        public HitKind Kind { get; private set; }
        public bool HasPoint { get; private set; }
        public Vector2 MapPoint { get; private set; }
        public Vector3 HitWorld { get; private set; }
        public bool TriggerDown { get; private set; }
        public bool TriggerHeld { get; private set; }
        public bool TriggerUp { get; private set; }
        public bool Valid { get; private set; }

        readonly RaycastHit[] _hits = new RaycastHit[512];
        bool _raw, _held;
        Material _lineMat, _reticleMat;

        void Awake()
        {
            if (_line != null)
            {
                _lineMat = _line.material;
                _line.useWorldSpace = true;
                _line.positionCount = 2;
                _line.enabled = false;
            }
            if (_reticle != null)
            {
                var r = _reticle.GetComponent<Renderer>();
                if (r != null) _reticleMat = r.material;
                _reticle.gameObject.SetActive(false);
            }
        }

        void OnDestroy()
        {
            if (_lineMat != null) Destroy(_lineMat);
            if (_reticleMat != null) Destroy(_reticleMat);
        }

        void OnDisable() => Hide();

        public void SetValidity(bool valid) => Valid = valid;

        void LateUpdate()
        {
            // Trigger edge detection always runs, so enabling the pointer mid-press does not fire a Down.
            float t = OVRInput.Get(OVRInput.RawAxis1D.RIndexTrigger);
            bool rising = false, falling = false;
            if (!_raw && t >= _pressThreshold) { _raw = true; rising = true; }
            else if (_raw && t <= _releaseThreshold) { _raw = false; falling = true; }

            TriggerDown = TriggerUp = TriggerHeld = false;
            Kind = HitKind.None;
            HasPoint = false;

            if (!Operative || _rayInteractor == null)
            {
                _held = false;
                Hide();
                return;
            }

            bool uiHover = _rayInteractor.HasCandidate;
            if (rising && !uiHover) _held = true;
            TriggerDown = rising && !uiHover;
            TriggerUp = falling && _held;
            if (falling) _held = false;
            TriggerHeld = _held;

            var ray = CurrentRay;
            var origin = ray.origin;
            var dir = ray.direction;
            if (uiHover || dir.sqrMagnitude < 0.5f) { Hide(); return; }

            // The operations panel's colliders are triggers (see OperationsPanel), so triggers are collected
            // and every trigger that does not belong to the panel is skipped below.
            int n = Physics.RaycastNonAlloc(origin, dir, _hits, _maxDistance, ~0, QueryTriggerInteraction.Collide);
            Array.Sort(_hits, 0, n, HitComparer.Instance);

            Vector3 end = origin + dir * _maxDistance;
            Vector3 normal = Vector3.up;
            for (int i = 0; i < n; i++)
            {
                var hit = _hits[i];
                var tr = hit.collider.transform;
                if (_ignoreRoot != null && tr.IsChildOf(_ignoreRoot)) continue;
                if (tr.IsChildOf(transform)) continue;
                if (hit.collider.isTrigger && hit.collider.GetComponentInParent<OperationsPanel>() == null) continue;
                Resolve(hit);
                end = hit.point;
                normal = hit.normal;
                break;
            }

            if (_line != null)
            {
                _line.enabled = true;
                _line.SetPosition(0, origin);
                _line.SetPosition(1, end);
                if (_lineMat != null) _lineMat.SetColor(BaseColor, Valid ? _validColor : _invalidColor);
            }
            if (_reticle != null)
            {
                bool show = Kind != HitKind.None;
                _reticle.gameObject.SetActive(show);
                if (show)
                {
                    float s = Kind == HitKind.Panel ? _reticleSizePanel : _reticleSizeFloor;
                    _reticle.position = end + normal * 0.002f;
                    _reticle.rotation = Quaternion.FromToRotation(Vector3.up, normal);
                    _reticle.localScale = new Vector3(s, s * 0.1f, s);
                    if (_reticleMat != null) _reticleMat.SetColor(BaseColor, Valid ? _validColor : _invalidColor);
                }
            }
        }

        void Resolve(RaycastHit hit)
        {
            var surface = hit.collider.GetComponentInParent<PanelSurface>();
            if (surface != null && surface.Panel != null)
            {
                if (surface.Panel.TryLocalToMap(hit.point, out var mp))
                {
                    Kind = HitKind.Panel;
                    HasPoint = true;
                    MapPoint = mp;
                    HitWorld = hit.point;
                }
                else Kind = HitKind.Blocked;
                return;
            }

            var reg = SimWorldRegistry.Instance;
            bool inWorld = reg != null && reg.Root != null && hit.collider.transform.IsChildOf(reg.Root);
            if (inWorld && hit.normal.y >= _floorMinNormalY && hit.point.y <= _floorMaxHeight)
            {
                Kind = HitKind.Floor;
                HasPoint = true;
                MapPoint = MapGridMath.WorldToMap(hit.point);
                HitWorld = hit.point;
                return;
            }
            Kind = HitKind.Blocked;
            HitWorld = hit.point;
        }

        void Hide()
        {
            if (_line != null) _line.enabled = false;
            if (_reticle != null) _reticle.gameObject.SetActive(false);
        }

        sealed class HitComparer : System.Collections.Generic.IComparer<RaycastHit>
        {
            public static readonly HitComparer Instance = new HitComparer();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }
    }
}
