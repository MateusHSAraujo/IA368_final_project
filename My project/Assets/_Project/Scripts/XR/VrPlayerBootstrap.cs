using System;
using System.Collections;
using Oculus.Interaction;
using Oculus.Interaction.Locomotion;
using UnityEngine;
using UnityEngine.AI;
using LocomotionCharacter = Oculus.Interaction.Locomotion.CharacterController;

namespace VRRobot.XR
{
    /// <summary>
    /// Keeps locomotion disabled until the runtime NavMesh exists, then places the player on it. With
    /// <c>_spawnOnNavMeshReady</c> off, the first placement waits for <see cref="RequestSpawn"/> (start flow).
    /// </summary>
    public class VrPlayerBootstrap : MonoBehaviour
    {
        [SerializeField] FirstPersonLocomotor _locomotor;
        [SerializeField] LocomotionCharacter _character;
        [SerializeField] SimWorldNavMeshBaker _baker;
        [SerializeField, Optional] Transform _spawnPoint;
        [SerializeField, Tooltip("Off: the first spawn waits for RequestSpawn() (e.g. after the start menu).")]
        bool _spawnOnNavMeshReady = true;
        [SerializeField] float _maxSpawnDistance = 20f;
        [SerializeField] float _onNavMeshTolerance = 0.5f;
        [SerializeField, Tooltip("Search radius for a spot where the capsule touches no collider.")]
        float _clearSearchRadius = 5f;
        [SerializeField, Tooltip("A spawn on a NavMesh island smaller than this (m²) moves to the largest overlapping island.")]
        float _minSpawnIslandArea = 4f;

        const float SpawnSearchStep = 0.25f;

        [Header("Recovery guard")]
        [SerializeField] float _fallDistance = 1.0f;
        [SerializeField] float _maxPenetration = 0.05f;
        [SerializeField] int _penetrationFrames = 3;
        [SerializeField] float _validSampleDistance = 0.3f;

        const float SampleInterval = 0.1f;
        const float RecoveryCooldown = 0.5f;
        static readonly Collider[] s_overlaps = new Collider[16];

        Vector3 _defaultSpawn;
        bool _placedOnce;
        CapsuleCollider _capsule;
        Vector3 _lastValid;
        bool _hasLastValid;
        float _nextSampleTime;
        float _cooldownEnd;
        int _penetratingFrames;

        bool _guardPaused;
        bool _spawnRequested;

        public bool IsLocomotionReady { get; private set; }

        /// <summary>Raised after the first placement, once locomotion is enabled.</summary>
        public event Action Spawned;

        /// <summary>Raised when the requested spawn is impossible (no walkable NavMesh near the spawn point).</summary>
        public event Action<string> SpawnFailed;

        /// <summary>Places the player as soon as the NavMesh is ready (immediately if it already is).</summary>
        public void RequestSpawn()
        {
            _spawnRequested = true;
            if (_baker.IsNavMeshReady) OnNavMeshReady();
        }

        /// <summary>While true the recovery guard does nothing (e.g. fly mode). Resuming starts a cooldown.</summary>
        public bool GuardPaused
        {
            get => _guardPaused;
            set
            {
                if (_guardPaused == value) return;
                _guardPaused = value;
                if (!value)
                {
                    _penetratingFrames = 0;
                    _cooldownEnd = Time.time + RecoveryCooldown;
                }
            }
        }

        void Awake()
        {
            _capsule = _character.GetComponent<CapsuleCollider>();
            _defaultSpawn = _character.transform.root.position; // rig origin = tracking floor
            _locomotor.DisableMovement();
            Debug.Log("[XRLoco] Locomotion disabled until the NavMesh is ready", this);
        }

        void OnEnable()
        {
            _baker.NavMeshReady += OnNavMeshReady;
            if (_baker.IsNavMeshReady) OnNavMeshReady();
        }

        void OnDisable() => _baker.NavMeshReady -= OnNavMeshReady;

        Vector3 FeetPosition() => CapsuleClearance.FeetOf(_character);

        // Safety net: Meta's CapsuleCast ignores colliders the capsule already overlaps, so the player can end
        // up inside geometry and fall out of the house. Teleport back to the last valid NavMesh position.
        void Update()
        {
            if (!IsLocomotionReady || _capsule == null || _guardPaused) return;

            var feet = FeetPosition();
            var depth = MaxPenetration(out var worst);
            _penetratingFrames = depth > _maxPenetration ? _penetratingFrames + 1 : 0;

            if (_character.IsGrounded && _penetratingFrames == 0 && Time.time >= _nextSampleTime)
            {
                _nextSampleTime = Time.time + SampleInterval;
                if (NavMesh.SamplePosition(feet, out var hit, _validSampleDistance, _baker.QueryFilter)
                    && CapsuleClearance.IsClear(_character, hit.position))
                {
                    _lastValid = hit.position;
                    _hasLastValid = true;
                }
            }

            if (!_hasLastValid || Time.time < _cooldownEnd) return;
            var fell = feet.y < _lastValid.y - _fallDistance;
            if (!fell && _penetratingFrames < _penetrationFrames) return;

            var reason = fell ? "fell" : $"inside {(worst != null ? worst.name : "?")} ({depth.ToString("F2")} m)";
            _locomotor.HandleLocomotionEvent(new LocomotionEvent(GetInstanceID(), _lastValid,
                LocomotionEvent.TranslationType.Absolute));
            _penetratingFrames = 0;
            _cooldownEnd = Time.time + RecoveryCooldown;
            Debug.LogWarning($"[XRLoco] Player recovered to {_lastValid} ({reason})", this);
        }

        float MaxPenetration(out Collider worst)
        {
            worst = null;
            float max = 0f;
            float half = Mathf.Max(0f, _character.Height * 0.5f - _character.Radius);
            var center = _character.Pose.position;
            int n = Physics.OverlapCapsuleNonAlloc(center + Vector3.up * half, center + Vector3.down * half,
                _character.Radius - 0.02f, s_overlaps, _character.LayerMask, QueryTriggerInteraction.Ignore);
            var t = _capsule.transform;
            var rigRoot = _character.transform.root;
            for (int i = 0; i < n; i++)
            {
                var other = s_overlaps[i];
                if (other == _capsule || other.transform.root == rigRoot) continue; // same rule as CapsuleClearance
                if (Physics.ComputePenetration(_capsule, t.position, t.rotation, other, other.transform.position,
                        other.transform.rotation, out _, out float d) && d > max)
                {
                    max = d;
                    worst = other;
                }
            }
            return max;
        }

        void OnNavMeshReady()
        {
            if (!_placedOnce && !_spawnOnNavMeshReady && !_spawnRequested) return;
            var filter = _baker.QueryFilter;
            if (_placedOnce && _guardPaused)
            {
                // Fly mode (or another owner) moves the rig: leave it alone; landing is resolved when it ends.
                Debug.Log("[XRLoco] NavMesh rebuilt while the guard is paused; player left where they are", this);
                return;
            }
            if (_placedOnce && NavMesh.SamplePosition(FeetPosition(), out _, _onNavMeshTolerance, filter))
            {
                Debug.Log("[XRLoco] NavMesh rebuilt; player still on it", this);
                return;
            }

            var spawn = _spawnPoint != null ? _spawnPoint.position : _defaultSpawn;
            // 1) Clear NavMesh point at the spawn's own level, searching outwards in rings (the NavMesh is
            //    approximate near obstacles and may also cover other levels, e.g. the outside ground).
            if (!CapsuleClearance.TryFindClearPoint(_character, spawn, filter, _clearSearchRadius, out var target, SpawnSearchStep))
            {
                // 2) Fallback: closest NavMesh point at any level, then the closest clear spot around it.
                if (!NavMesh.SamplePosition(spawn, out var hit, _maxSpawnDistance, filter))
                {
                    var reason = $"no walkable area within {_maxSpawnDistance.ToString("F0")} m of the spawn point";
                    Debug.LogWarning("[XRLoco] Spawn failed: " + reason, this);
                    if (!_placedOnce) SpawnFailed?.Invoke(reason);
                    return;
                }
                if (!CapsuleClearance.TryFindClearPoint(_character, hit.position, filter, _clearSearchRadius, out target, SpawnSearchStep))
                {
                    Debug.LogWarning($"[XRLoco] No clear spot near {hit.position}; using the NavMesh point", this);
                    target = hit.position;
                }
            }

            if (_baker.TryGetIsland(target, out var island) && island.Area < _minSpawnIslandArea)
            {
                foreach (var cand in _baker.Islands) // sorted by area, descending: the first that works is the largest
                {
                    if (cand == island || cand.Area <= island.Area) continue;
                    if (cand.Bounds.min.y > spawn.y + 0.5f || cand.Bounds.max.y < spawn.y - 0.5f) continue;
                    if (!CapsuleClearance.TryFindClearPoint(_character, cand.ClosestPoint(spawn), filter, _clearSearchRadius,
                            out var better, SpawnSearchStep)) continue;
                    Debug.Log($"[XRLoco] Spawn island too small ({island.Area.ToString("F1")} m²); using island of {cand.Area.ToString("F1")} m²", this);
                    target = better;
                    break;
                }
            }

            _placedOnce = true;
            _lastValid = target;
            _hasLastValid = true;
            _locomotor.HandleLocomotionEvent(new LocomotionEvent(GetInstanceID(), target,
                LocomotionEvent.TranslationType.Absolute));
            StartCoroutine(EnableNextFrame(target));
        }

        /// <summary>
        /// Where the player should land when fly mode ends above <paramref name="ground"/> (the surface under the head).
        /// Lands there if it is walkable and on the same NavMesh island as the last valid position; otherwise returns the
        /// closest clear point of that island (or the last valid position). Without a NavMesh, returns the ground.
        /// </summary>
        public Vector3 ResolveLanding(Vector3 ground)
        {
            if (!IsLocomotionReady || !_hasLastValid || !_baker.IsNavMeshReady) return ground;
            var filter = _baker.QueryFilter;
            _baker.TryGetIsland(_lastValid, out var home);
            if (NavMesh.SamplePosition(ground, out var hit, _validSampleDistance, filter)
                && Mathf.Abs(hit.position.y - ground.y) < _validSampleDistance
                && (home == null || (_baker.TryGetIsland(hit.position, out var island) && island == home))
                && CapsuleClearance.IsClear(_character, hit.position))
                return hit.position;

            var near = home != null ? home.ClosestPoint(ground) : _lastValid;
            var target = CapsuleClearance.TryFindClearPoint(_character, near, filter, _clearSearchRadius, out var clear, SpawnSearchStep)
                ? clear : _lastValid;
            Debug.Log($"[XRLoco] Landing at {ground} is not walkable from the current area; moving to {target}", this);
            return target;
        }

        /// <summary>Teleports the character's feet to <paramref name="feet"/> through the locomotor (absolute event).</summary>
        public void TeleportTo(Vector3 feet)
        {
            _locomotor.HandleLocomotionEvent(new LocomotionEvent(GetInstanceID(), feet, LocomotionEvent.TranslationType.Absolute));
            _lastValid = feet;
            _hasLastValid = true;
        }

        IEnumerator EnableNextFrame(Vector3 position)
        {
            yield return null;
            _locomotor.EnableMovement();
            bool first = !IsLocomotionReady;
            IsLocomotionReady = true;
            Debug.Log($"[XRLoco] Player placed at {position} and locomotion enabled", this);
            if (first) Spawned?.Invoke();
        }
    }
}
