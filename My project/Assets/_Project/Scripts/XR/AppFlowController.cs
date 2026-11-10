using System.Collections;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Locomotion;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VRRobot.Core;
using VRRobot.SimWorld;

namespace VRRobot.XR
{
    /// <summary>
    /// Start flow of the VR app: main menu (the CoppeliaSim import starts only when the user confirms) ->
    /// elevated overview while the scene is built -> spawn on the walkable area, with locomotion and the
    /// in-game menu enabled. If the player cannot be placed (no NavMesh, no walkable area near the spawn,
    /// timeouts), an error is shown and the player starts at the CoppeliaSim origin in fly mode.
    /// Later resynchronizations of the scene do not go back to the overview.
    /// </summary>
    public class AppFlowController : MonoBehaviour
    {
        public enum Phase { MainMenu, Loading, Spawning, Playing }

        [Header("World")]
        [SerializeField] CoppeliaSceneBuilder _builder;
        [SerializeField] SimWorldRegistry _registry;
        [SerializeField] SimWorldNavMeshBaker _baker;

        [Header("Player")]
        [SerializeField] VrPlayerBootstrap _bootstrap;
        [SerializeField] FirstPersonLocomotor _locomotor;
        [SerializeField] LocomotionInputGate _gate;
        [SerializeField] VrMenuController _inGameMenu;
        [SerializeField] VrFlyMode _flyMode;
        [SerializeField, Tooltip("Camera Rig root.")] Transform _rigRoot;
        [SerializeField, Tooltip("CenterEyeAnchor.")] Transform _head;
        [SerializeField, Optional] OVRScreenFade _fade;

        [Header("Start panel")]
        [SerializeField, Tooltip("World-space canvas root.")] Transform _panel;
        [SerializeField] GameObject _mainPage;
        [SerializeField] GameObject _loadingPage;
        [SerializeField] Button _loadButton;
        [SerializeField] TMP_Text _endpointLabel;
        [SerializeField] TMP_Text _loadingTitle;
        [SerializeField] TMP_Text _statusLabel;
        [SerializeField] GameObject _progressBar;
        [SerializeField, Tooltip("Progress bar fill, stretched horizontally through its anchors.")]
        RectTransform _progressFill;
        [SerializeField, Tooltip("Shown with the spawn error; hides the panel.")] Button _dismissButton;
        [SerializeField] float _menuDistance = 1.0f;
        [SerializeField, Tooltip("While loading the panel sits small and slightly above eye level: the overview keeps the " +
                                 "whole scene below the horizon, so the panel does not cover it.")]
        float _loadingDistance = 0.7f;
        [SerializeField] float _loadingHeight = 0.12f;
        [SerializeField, Tooltip("Positive tilts the panel face up, negative down (towards the eyes when above them).")]
        float _loadingTilt = -10f;
        [SerializeField] float _loadingScale = 0.6f;

        [Header("Overview")]
        [SerializeField, Tooltip("Angle below the horizon of the scene center, seen from the viewpoint. The head looks " +
                                 "horizontally, so elevation + half view angle must stay within the headset's lower field of view.")]
        float _elevation = 20f;
        [SerializeField, Tooltip("Half of the view angle the scene must fit in.")]
        float _halfViewAngle = 20f;
        [SerializeField] float _minDistance = 3f;

        [Header("Fallback")]
        [SerializeField, Tooltip("Seconds after the scene is built to wait for the NavMesh.")] float _navMeshTimeout = 15f;
        [SerializeField, Tooltip("Seconds to wait for the spawn after requesting it.")] float _spawnTimeout = 5f;

        Phase _phase = Phase.MainMenu;
        Vector3 _panelScale;
        Vector3 _viewDirection;
        bool _hierarchyApplied, _overviewDone, _sceneBuilt, _navMeshReady, _spawned, _fallingBack, _black;
        float _sceneBuiltTime;
        string _lastError, _navMeshError, _spawnError;
        Coroutine _spawnRoutine;

        public Phase CurrentPhase => _phase;

        /// <summary>true when the player started at the origin in fly mode because the normal spawn failed.</summary>
        public bool UsedFallback { get; private set; }

        void Awake()
        {
            _panelScale = _panel.localScale;
            _viewDirection = _rigRoot.forward;
            _loadButton.onClick.AddListener(OnLoadClicked);
            _dismissButton.onClick.AddListener(() => _panel.gameObject.SetActive(false));
            if (_fade != null) _fade.fadeOnStart = false;
        }

        void OnEnable()
        {
            _registry.HierarchyApplied += OnHierarchyApplied;
            _registry.SceneBuilt += OnSceneBuilt;
            _baker.NavMeshReady += OnNavMeshReady;
            _baker.NavMeshFailed += OnNavMeshFailed;
            _bootstrap.Spawned += OnSpawned;
            _bootstrap.SpawnFailed += OnSpawnFailed;
        }

        void OnDisable()
        {
            _registry.HierarchyApplied -= OnHierarchyApplied;
            _registry.SceneBuilt -= OnSceneBuilt;
            _baker.NavMeshReady -= OnNavMeshReady;
            _baker.NavMeshFailed -= OnNavMeshFailed;
            _bootstrap.Spawned -= OnSpawned;
            _bootstrap.SpawnFailed -= OnSpawnFailed;
        }

        IEnumerator Start()
        {
            if (_builder.buildOnStart)
                Debug.LogWarning("[XRFlow] The scene builder has buildOnStart on; the main menu will not delay the import", this);
            _inGameMenu.enabled = false;
            _gate.Acquire(this);
            _locomotor.DisableMovement();
            _locomotor.enabled = false; // it re-places the rig every frame; the flow moves the rig itself
            _endpointLabel.text = $"CoppeliaSim streamer: {_builder.host}:{_builder.requestPort}";
            _mainPage.SetActive(true);
            _loadingPage.SetActive(false);
            _dismissButton.gameObject.SetActive(false);
            _panel.gameObject.SetActive(false);
            yield return null;
            // OVRScreenFade with fadeOnStart off leaves its quad enabled without a material (drawn magenta)
            // until the first alpha update.
            if (_fade != null) _fade.SetExplicitFade(0f);
            // Place the panel only once the head is tracked (its local pose starts at the rig origin).
            float timeout = Time.realtimeSinceStartup + 3f;
            while (_head.localPosition.sqrMagnitude < 0.01f && Time.realtimeSinceStartup < timeout) yield return null;
            yield return null;
            PlacePanel(false);
            _panel.gameObject.SetActive(true);
            Debug.Log("[XRFlow] Main menu", this);
        }

        void OnLoadClicked()
        {
            if (_phase != Phase.MainMenu) return;
            _phase = Phase.Loading;
            _mainPage.SetActive(false);
            _loadingPage.SetActive(true);
            UpdateStatus();
            Debug.Log("[XRFlow] Import confirmed; building the scene", this);
            _builder.Build();
        }

        void OnHierarchyApplied()
        {
            if (_phase != Phase.Loading || _hierarchyApplied) return;
            _hierarchyApplied = true;
            StartCoroutine(MoveToOverview());
        }

        IEnumerator MoveToOverview()
        {
            yield return FadeOut();
            var points = new List<Vector3>();
            foreach (var o in _registry.Objects.Values)
                if (o.IsShape && o.VisibleInSim && o.Transform != null) points.Add(o.Transform.position);
            var view = OverviewFraming.Compute(points, _viewDirection, _elevation, _halfViewAngle, _minDistance);

            float headYaw = (Quaternion.Inverse(_rigRoot.rotation) * _head.rotation).eulerAngles.y;
            _rigRoot.rotation = Quaternion.Euler(0f, view.Yaw - headYaw, 0f);
            _rigRoot.position += view.Eye - _head.position;
            PlacePanel(true);
            Debug.Log($"[XRFlow] Overview at {view.Eye} looking at {view.Target} (radius {view.Radius.ToString("F1")} m, {points.Count} shapes)", this);
            yield return FadeIn();
            _overviewDone = true;
        }

        void OnSceneBuilt()
        {
            if (_phase != Phase.Loading || _sceneBuilt) return;
            _sceneBuilt = true;
            _sceneBuiltTime = Time.time;
        }

        void OnNavMeshReady()
        {
            if (_phase != Phase.Loading || !_sceneBuilt) return;
            _navMeshReady = true;
        }

        void OnNavMeshFailed(string reason)
        {
            if (_phase == Phase.Loading) _navMeshError = reason;
        }

        void OnSpawnFailed(string reason) => _spawnError = reason;

        void OnSpawned() => _spawned = true;

        void Update()
        {
            if (_phase != Phase.Loading) return;
            UpdateStatus();
            if (!_sceneBuilt || !_overviewDone) return;
            if (_navMeshReady)
            {
                _spawnRoutine = StartCoroutine(Spawn());
                return;
            }
            // The baker may have failed before this flow subscribed (it disables itself without a registry or agent type).
            string failure = _navMeshError ?? (!_baker.isActiveAndEnabled ? _baker.FailureReason ?? "the NavMesh baker is disabled" : null);
            if (failure == null && Time.time - _sceneBuiltTime > _navMeshTimeout)
                failure = $"the walkable area was not ready after {_navMeshTimeout.ToString("F0")} s";
            if (failure != null) StartCoroutine(Fallback("Could not compute the walkable area: " + failure));
        }

        IEnumerator Spawn()
        {
            _phase = Phase.Spawning;
            UpdateStatus();
            yield return new WaitForSeconds(0.5f); // show the finished scene for a moment
            yield return FadeOut();
            _panel.gameObject.SetActive(false);
            _locomotor.enabled = true;
            yield return null; // let the locomotor start before sending it the spawn event
            _spawnError = null;
            _bootstrap.RequestSpawn();
            float deadline = Time.time + _spawnTimeout;
            while (!_spawned && _spawnError == null && Time.time < deadline) yield return null;
            if (!_spawned)
            {
                _spawnRoutine = null;
                StartCoroutine(Fallback("Could not place the player in the scene: " +
                                        (_spawnError ?? $"no spawn after {_spawnTimeout.ToString("F0")} s")));
                yield break;
            }
            _gate.Release(this);
            _inGameMenu.enabled = true;
            _phase = Phase.Playing;
            _spawnRoutine = null;
            Debug.Log("[XRFlow] Playing", this);
            yield return FadeIn();
        }

        /// <summary>Starts the player at the CoppeliaSim origin (feet on z = 0) in fly mode and explains why.</summary>
        IEnumerator Fallback(string message)
        {
            if (_fallingBack) yield break;
            _fallingBack = true;
            _phase = Phase.Spawning;
            if (_spawnRoutine != null) { StopCoroutine(_spawnRoutine); _spawnRoutine = null; }
            Debug.LogWarning("[XRFlow] " + message + ". Starting at the CoppeliaSim origin in fly mode.", this);
            yield return FadeOut();

            _flyMode.SetFlying(true); // no collisions, locomotor off, recovery guard paused
            var p = _rigRoot.position + new Vector3(-_head.position.x, 0f, -_head.position.z);
            p.y = _locomotor.HeightOffset; // rig origin sits HeightOffset above the feet
            _rigRoot.position = p;
            FaceOpenDirection();

            _loadingTitle.text = "Could not place the player";
            _statusLabel.text = $"<size=80%>{message}.</size>\n<size=70%>Fly mode is on (no collisions). Turn it off in the menu " +
                                "(left Menu button > Settings) above a floor.</size>";
            if (_progressBar != null) _progressBar.SetActive(false);
            _dismissButton.gameObject.SetActive(true);
            _loadingPage.SetActive(true);
            PlacePanel(false);
            _panel.gameObject.SetActive(true);

            _gate.Release(this);
            _inGameMenu.enabled = true;
            UsedFallback = true;
            _phase = Phase.Playing;
            yield return FadeIn();
        }

        static readonly Collider[] s_overlaps = new Collider[16];

        /// <summary>
        /// Turns the rig (around the head) towards the closest direction where the message panel fits without
        /// touching any collider and is not hidden behind one (the CoppeliaSim origin may sit right next to a wall).
        /// </summary>
        void FaceOpenDirection()
        {
            var rigRoot = _rigRoot.root;
            var forward = Vector3.ProjectOnPlane(_head.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            var rect = (RectTransform)_panel;
            var halfExtents = new Vector3(rect.rect.width * _panelScale.x, rect.rect.height * _panelScale.y, 0f) * 0.5f
                              + new Vector3(0.05f, 0.05f, 0.1f);
            // 0, +30, -30, +60, -60, ... 180
            for (int i = 0; i <= 12; i++)
            {
                float angle = ((i + 1) / 2) * 30f * (i % 2 == 1 ? 1f : -1f);
                var dir = Quaternion.Euler(0f, angle, 0f) * forward;
                var center = _head.position + dir * _menuDistance;
                if (Blocked(center, halfExtents, Quaternion.LookRotation(dir), rigRoot)) continue;
                if (angle != 0f)
                {
                    _rigRoot.RotateAround(_head.position, Vector3.up, angle);
                    Debug.Log($"[XRFlow] Turned {angle.ToString("F0")}° so the message panel is not inside a wall", this);
                }
                return;
            }
        }

        bool Blocked(Vector3 center, Vector3 halfExtents, Quaternion rotation, Transform rigRoot)
        {
            int n = Physics.OverlapBoxNonAlloc(center, halfExtents, s_overlaps, rotation, ~0, QueryTriggerInteraction.Ignore);
            for (int k = 0; k < n; k++)
                if (s_overlaps[k].transform.root != rigRoot) return true;
            var toCenter = center - _head.position;
            return Physics.Raycast(_head.position, toCenter.normalized, out var hit, toCenter.magnitude, ~0, QueryTriggerInteraction.Ignore)
                   && hit.collider.transform.root != rigRoot;
        }

        void UpdateStatus()
        {
            // Keep the last error on screen while the builder retries (it alternates Error/Connecting).
            var state = _builder.State;
            if (state == CoppeliaSceneBuilder.BuildState.Error) _lastError = _builder.StatusMessage;
            else if (state != CoppeliaSceneBuilder.BuildState.Connecting) _lastError = null;

            // Only rebuild the text when something it shows changed (no string/TMP mesh rebuild every frame).
            var key = (_phase, state, _builder.ShapesBuilt, _builder.ShapesTotal, _lastError);
            if (key.Equals(_shownKey)) return;
            _shownKey = key;

            string text;
            float progress = _builder.LoadProgress;
            if (_phase == Phase.Spawning)
            {
                text = "Scene ready";
                progress = 1f;
            }
            else if (_lastError != null)
                text = $"Retrying connection...\n<size=70%>{_lastError}</size>";
            else
                text = state switch
                {
                    CoppeliaSceneBuilder.BuildState.BuildingMeshes => $"Building meshes: {_builder.ShapesBuilt} / {_builder.ShapesTotal}",
                    CoppeliaSceneBuilder.BuildState.Ready => "Preparing the walkable area...",
                    CoppeliaSceneBuilder.BuildState.Listing => "Reading the scene...",
                    _ => "Connecting to CoppeliaSim...",
                };
            _statusLabel.text = text;
            if (_progressFill != null)
            {
                var max = _progressFill.anchorMax;
                max.x = Mathf.Clamp01(progress);
                _progressFill.anchorMax = max;
            }
        }

        (Phase, CoppeliaSceneBuilder.BuildState, int, int, string) _shownKey;

        void PlacePanel(bool loading)
        {
            var flat = Vector3.ProjectOnPlane(_head.forward, Vector3.up);
            flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : _rigRoot.forward;
            float distance = loading ? _loadingDistance : _menuDistance;
            float height = loading ? _loadingHeight : 0f;
            var rotation = Quaternion.LookRotation(flat, Vector3.up) * Quaternion.Euler(loading ? _loadingTilt : 0f, 0f, 0f);
            _panel.SetPositionAndRotation(_head.position + flat * distance + Vector3.up * height, rotation);
            _panel.localScale = _panelScale * (loading ? _loadingScale : 1f);
        }

        // Fades are idempotent: fading out while already black (e.g. a fallback during the spawn) does not flash.
        IEnumerator FadeOut()
        {
            if (_fade == null || _black) yield break;
            _black = true;
            _fade.FadeOut();
            yield return new WaitForSeconds(_fade.fadeTime);
        }

        IEnumerator FadeIn()
        {
            if (_fade == null || !_black) yield break;
            _black = false;
            _fade.FadeIn();
            yield return new WaitForSeconds(_fade.fadeTime);
        }
    }
}
