using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.AI;
using LocomotionCharacter = Oculus.Interaction.Locomotion.CharacterController;

namespace VRRobot.XR
{
    /// <summary>
    /// Wraps another ISurface (Meta's NavMeshSurface) and only accepts points that are reachable
    /// on foot from the player's current position (NavMesh path complete).
    /// </summary>
    public class ReachableTeleportSurface : MonoBehaviour, ISurface
    {
        [SerializeField, Interface(typeof(ISurface))]
        UnityEngine.Object _surface;
        [SerializeField] LocomotionCharacter _character;
        [SerializeField, Optional] SimWorldNavMeshBaker _baker;
        [SerializeField] float _sampleDistance = 0.3f;

        const float CacheTolerance = 0.05f;

        ISurface Surface;
        NavMeshPath _path;
        NavMeshQueryFilter _ownFilter;
        bool _hasOwnFilter;

        int _lastFrame = -10;
        Vector3 _lastTarget, _lastFeet;
        bool _lastResult;

        public Transform Transform => Surface.Transform;

        void Awake()
        {
            Surface = _surface as ISurface;
            _path = new NavMeshPath();
        }

        public bool Raycast(in Ray ray, out SurfaceHit hit, float maxDistance = 0)
        {
            if (!Surface.Raycast(in ray, out hit, maxDistance)) return false;
            return IsReachable(hit.Point);
        }

        public bool ClosestSurfacePoint(in Vector3 point, out SurfaceHit hit, float maxDistance = 0)
        {
            if (!Surface.ClosestSurfacePoint(in point, out hit, maxDistance)) return false;
            return IsReachable(hit.Point);
        }

        /// <summary>true if a complete NavMesh path exists from the player's feet to the target.</summary>
        public bool IsReachable(Vector3 target)
        {
            if (_baker != null && !_baker.IsNavMeshReady) return false;
            if (!TryGetFilter(out var filter)) return false;

            var feet = CapsuleClearance.FeetOf(_character);
            int frame = Time.frameCount;
            if (frame - _lastFrame <= 1 &&
                (target - _lastTarget).sqrMagnitude < CacheTolerance * CacheTolerance &&
                (feet - _lastFeet).sqrMagnitude < CacheTolerance * CacheTolerance)
            {
                _lastFrame = frame;
                return _lastResult;
            }

            bool ok = NavMesh.SamplePosition(feet, out var from, _sampleDistance, filter)
                   && NavMesh.SamplePosition(target, out var to, _sampleDistance, filter)
                   && NavMesh.CalculatePath(from.position, to.position, filter, _path)
                   && _path.status == NavMeshPathStatus.PathComplete
                   && CapsuleClearance.IsClear(_character, to.position);

            _lastFrame = frame;
            _lastTarget = target;
            _lastFeet = feet;
            _lastResult = ok;
            return ok;
        }

        bool TryGetFilter(out NavMeshQueryFilter filter)
        {
            if (_baker != null) { filter = _baker.QueryFilter; return true; }
            if (!_hasOwnFilter)
            {
                if (!SimWorldNavMeshBaker.TryResolveAgentType("VRPlayer", out var id))
                {
                    filter = default;
                    return false;
                }
                _ownFilter = new NavMeshQueryFilter { agentTypeID = id, areaMask = NavMesh.AllAreas };
                _hasOwnFilter = true;
            }
            filter = _ownFilter;
            return true;
        }

        #region Inject

        public void InjectAllReachableTeleportSurface(ISurface surface, LocomotionCharacter character)
        {
            _surface = surface as UnityEngine.Object;
            Surface = surface;
            _character = character;
        }

        public void InjectOptionalBaker(SimWorldNavMeshBaker baker) => _baker = baker;

        #endregion
    }
}
