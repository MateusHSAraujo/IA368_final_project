using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;
using VRRobot.SimWorld;
using Debug = UnityEngine.Debug;

namespace VRRobot.XR
{
    /// <summary>
    /// Bakes a runtime NavMesh from the colliders under <see cref="SimWorldRegistry.Root"/> every time the
    /// scene is (re)built. Uses the low-level NavMeshBuilder API with the "VRPlayer" agent type.
    /// </summary>
    public class SimWorldNavMeshBaker : MonoBehaviour
    {
        [SerializeField, Tooltip("Optional; falls back to SimWorldRegistry.Instance.")]
        SimWorldRegistry registry;
        [SerializeField] string agentTypeName = "VRPlayer";
        [SerializeField] LayerMask includedLayers = ~0;
        [SerializeField] float boundsPadding = 1f;

        SimWorldRegistry _registry;
        NavMeshData _data;
        NavMeshDataInstance _instance;
        bool _baking, _pending;
        Coroutine _routine;

        /// <summary>Raised on the main thread after each successful bake.</summary>
        public event Action NavMeshReady;

        /// <summary>Raised when a bake cannot produce a NavMesh (no sources, empty result).</summary>
        public event Action<string> NavMeshFailed;

        /// <summary>Why the last bake failed, or why the baker could not start (missing registry or agent type); null if fine.</summary>
        public string FailureReason { get; private set; }

        public bool IsNavMeshReady { get; private set; }
        public int AgentTypeId { get; private set; } = -1;
        public NavMeshQueryFilter QueryFilter => new NavMeshQueryFilter { agentTypeID = AgentTypeId, areaMask = NavMesh.AllAreas };
        public float LastBakeMs { get; private set; }
        public float LastArea { get; private set; }

        /// <summary>A connected component of the baked NavMesh (triangles sharing vertices, merged at 1 cm).</summary>
        public class Island
        {
            public float Area;
            public Bounds Bounds;
            internal readonly List<Vector3> Centroids = new List<Vector3>();

            /// <summary>Centroid of the island's triangle closest to <paramref name="p"/>.</summary>
            public Vector3 ClosestPoint(Vector3 p)
            {
                var best = Bounds.center;
                float bestD = float.MaxValue;
                foreach (var c in Centroids)
                {
                    float d = (c - p).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = c; }
                }
                return best;
            }
        }

        readonly List<Island> _islands = new List<Island>();
        /// <summary>Islands of the last bake, sorted by area (descending).</summary>
        public IReadOnlyList<Island> Islands => _islands;

        /// <summary>Island whose triangle centroid is closest to <paramref name="point"/> (within 0.5 m).</summary>
        public bool TryGetIsland(Vector3 point, out Island island)
        {
            island = null;
            float bestD = 0.5f * 0.5f;
            foreach (var isl in _islands)
                foreach (var c in isl.Centroids)
                {
                    float d = (c - point).sqrMagnitude;
                    if (d < bestD) { bestD = d; island = isl; }
                }
            return island != null;
        }

        // NavMesh.CalculateTriangulation returns every loaded NavMesh (all agent types): keep only the triangles
        // that this baker's agent type can query.
        // The triangulation is the simplified polygon mesh; queries use the detail mesh, which can sit a little
        // above/below a sloped polygon's centroid.
        const float OwnTriangleTolerance = 0.2f;

        int[] OwnTriangles(NavMeshTriangulation tri)
        {
            var filter = QueryFilter;
            var kept = new List<int>(tri.indices.Length);
            for (int i = 0; i + 2 < tri.indices.Length; i += 3)
            {
                var centroid = (tri.vertices[tri.indices[i]] + tri.vertices[tri.indices[i + 1]] + tri.vertices[tri.indices[i + 2]]) / 3f;
                if (!NavMesh.SamplePosition(centroid, out _, OwnTriangleTolerance, filter)) continue;
                kept.Add(tri.indices[i]); kept.Add(tri.indices[i + 1]); kept.Add(tri.indices[i + 2]);
            }
            return kept.ToArray();
        }

        void ComputeIslands(Vector3[] vertices, int[] indices)
        {
            _islands.Clear();
            var tri = new NavMeshTriangulation { vertices = vertices, indices = indices };
            int triCount = tri.indices.Length / 3;
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }

            var owner = new Dictionary<Vector3Int, int>();
            for (int t = 0; t < triCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    var v = tri.vertices[tri.indices[t * 3 + k]] * 100f;
                    var key = new Vector3Int(Mathf.RoundToInt(v.x), Mathf.RoundToInt(v.y), Mathf.RoundToInt(v.z));
                    if (owner.TryGetValue(key, out var other)) parent[Find(t)] = Find(other);
                    else owner[key] = t;
                }

            var byRoot = new Dictionary<int, Island>();
            for (int t = 0; t < triCount; t++)
            {
                var a = tri.vertices[tri.indices[t * 3]];
                var b = tri.vertices[tri.indices[t * 3 + 1]];
                var c = tri.vertices[tri.indices[t * 3 + 2]];
                int root = Find(t);
                if (!byRoot.TryGetValue(root, out var isl))
                {
                    isl = new Island { Bounds = new Bounds(a, Vector3.zero) };
                    byRoot[root] = isl;
                }
                isl.Area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                isl.Bounds.Encapsulate(a); isl.Bounds.Encapsulate(b); isl.Bounds.Encapsulate(c);
                isl.Centroids.Add((a + b + c) / 3f);
            }
            _islands.AddRange(byRoot.Values);
            _islands.Sort((x, y) => y.Area.CompareTo(x.Area));
        }

        string IslandSummary(CultureInfo ci)
        {
            var parts = new List<string>();
            for (int i = 0; i < Mathf.Min(3, _islands.Count); i++) parts.Add(_islands[i].Area.ToString("F1", ci));
            return string.Join(", ", parts);
        }

        void OnEnable()
        {
            _registry = registry != null ? registry : SimWorldRegistry.Instance;
            if (_registry == null)
            {
                FailureReason = "no SimWorldRegistry found";
                Debug.LogError("[XRLoco] SimWorldNavMeshBaker: no SimWorldRegistry found.", this);
                enabled = false;
                return;
            }
            if (!TryResolveAgentType(agentTypeName, out var id))
            {
                FailureReason = $"NavMesh agent type '{agentTypeName}' not found";
                Debug.LogError($"[XRLoco] NavMesh agent type '{agentTypeName}' not found in the project settings.", this);
                enabled = false;
                return;
            }
            AgentTypeId = id;
            _registry.SceneBuilt += RequestBake;
            if (_registry.IsReady) RequestBake();
        }

        void OnDisable()
        {
            if (_registry != null) _registry.SceneBuilt -= RequestBake;
            if (_routine != null) StopCoroutine(_routine);
            _routine = null;
            _baking = _pending = false;
            IsNavMeshReady = false;
            if (_instance.valid) _instance.Remove();
            if (_data != null) Destroy(_data);
            _data = null;
        }

        /// <summary>Resolves a NavMesh agent type id from its name in the project settings.</summary>
        public static bool TryResolveAgentType(string name, out int id)
        {
            for (int i = 0, n = NavMesh.GetSettingsCount(); i < n; i++)
            {
                var s = NavMesh.GetSettingsByIndex(i);
                if (NavMesh.GetSettingsNameFromID(s.agentTypeID) == name) { id = s.agentTypeID; return true; }
            }
            id = -1;
            return false;
        }

        void Fail(string reason)
        {
            FailureReason = reason;
            Debug.LogWarning("[XRLoco] NavMesh bake failed: " + reason, this);
            NavMeshFailed?.Invoke(reason);
        }

        void RequestBake()
        {
            if (_baking) { _pending = true; return; }
            _routine = StartCoroutine(Bake());
        }

        IEnumerator Bake()
        {
            _baking = true;
            do
            {
                _pending = false;
                var sources = new List<NavMeshBuildSource>();
                NavMeshBuilder.CollectSources(_registry.Root, includedLayers, NavMeshCollectGeometry.PhysicsColliders,
                    0, new List<NavMeshBuildMarkup>(), sources);

                bool any = false;
                var bounds = new Bounds();
                foreach (var c in _registry.Root.GetComponentsInChildren<Collider>())
                {
                    if (!c.enabled) continue;
                    if (!any) { bounds = c.bounds; any = true; }
                    else bounds.Encapsulate(c.bounds);
                }
                if (sources.Count == 0 || !any)
                {
                    Fail("no collider sources under the SimWorld root");
                    continue;
                }
                bounds.Expand(boundsPadding * 2f);

                if (_data == null)
                {
                    _data = new NavMeshData(AgentTypeId);
                    _instance = NavMesh.AddNavMeshData(_data);
                }

                var sw = Stopwatch.StartNew();
                var op = NavMeshBuilder.UpdateNavMeshDataAsync(_data, NavMesh.GetSettingsByID(AgentTypeId), sources, bounds);
                while (!op.isDone) yield return null;
                sw.Stop();

                var tri = NavMesh.CalculateTriangulation();
                var own = OwnTriangles(tri);
                LastBakeMs = (float)sw.Elapsed.TotalMilliseconds;
                ComputeIslands(tri.vertices, own);
                float area = 0f;
                foreach (var isl in _islands) area += isl.Area;
                LastArea = area;
                if (own.Length == 0)
                {
                    Fail("the bake produced an empty NavMesh");
                    continue;
                }

                var ci = CultureInfo.InvariantCulture;
                Debug.Log($"[XRLoco] NavMesh baked in {LastBakeMs.ToString("F1", ci)} ms: {sources.Count} sources, " +
                          $"{own.Length / 3} triangles, {area.ToString("F1", ci)} m², " +
                          $"{_islands.Count} islands (largest: {IslandSummary(ci)} m²)", this);
                FailureReason = null;
                IsNavMeshReady = true;
                NavMeshReady?.Invoke();
            } while (_pending);
            _baking = false;
            _routine = null;
        }
    }
}
