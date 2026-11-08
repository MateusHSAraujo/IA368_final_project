using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using VRRobot.Core;
using VRRobot.SimWorld.Building;
using VRRobot.SimWorld.Protocol;
using VRRobot.SimWorld.Transport;
using Debug = UnityEngine.Debug;

namespace VRRobot.SimWorld
{
    /// <summary>
    /// Rebuilds the CoppeliaSim scene: info -> list -> hierarchy (one GameObject per object) ->
    /// shape meshes, one at a time, with a per-frame budget. When the stream sends a "scene"
    /// event, it re-runs list and applies only the difference (create, remove, reparent).
    /// </summary>
    [RequireComponent(typeof(SimWorldRegistry))]
    public class CoppeliaSceneBuilder : MonoBehaviour
    {
        public enum BuildState { Idle, Connecting, Listing, BuildingMeshes, Ready, Error }

        [Header("Connection (zmq_scene_streamer.lua)")]
        public string host = "localhost";
        public int requestPort = 23005;
        [Tooltip("0 = use the posePort reported by 'info'")]
        public int posePort = 0;
        public int requestTimeoutMs = 20000;
        public float retryIntervalSeconds = 2f;

        [Header("Build")]
        public bool buildOnStart = true;
        [Tooltip("Disables the renderer of shapes that are invisible in CoppeliaSim (roof, resizable floor)")]
        public bool hideInvisibleShapes = true;
        public bool addMeshColliders = false;
        [Tooltip("Shadows on the rebuilt objects. CoppeliaSim does not render shadows; off saves the shadow map pass.")]
        public bool castShadows = false;
        [Tooltip("Main-thread time per frame spent creating meshes")]
        public float frameBudgetMs = 8f;
        [Tooltip("Mesh requests in flight (pipeline)")]
        [Range(1, 8)] public int prefetch = 3;
        public bool flipTexturesVertically = false;
        [Tooltip("Optional: material whose shader is used (default: URP/Lit). Keeps the shader in builds.")]
        public Material materialTemplate;

        // Observable state (HUD)
        public BuildState State { get; private set; } = BuildState.Idle;
        public string StatusMessage { get; private set; } = "";
        public InfoReply Info { get; private set; }
        public int ShapesTotal { get; private set; }
        public int ShapesBuilt { get; private set; }
        public int ObjectCount => _registry != null ? _registry.Objects.Count : 0;
        public long VertexCount { get; private set; }
        public long TriangleCount { get; private set; }
        public long SourceCorners { get; private set; }
        public int SubmeshCount { get; private set; }
        public float LastLoadSeconds { get; private set; }
        public float LoadProgress => ShapesTotal == 0 ? (State == BuildState.Ready ? 1f : 0f) : (float)ShapesBuilt / ShapesTotal;
        public MaterialCache Materials => _materials;
        public ZmqPoseStream PoseStream => _stream;
        public ZmqSceneClient Client => _client;

        SimWorldRegistry _registry;
        ZmqSceneClient _client;
        ZmqPoseStream _stream;
        MaterialCache _materials;
        Transform _root;
        readonly List<Mesh> _meshes = new List<Mesh>();
        readonly HashSet<int> _failedTextures = new HashSet<int>();
        Coroutine _run;
        bool _syncRequested;

        // References are fetched in OnEnable (not Awake): after a domain reload in Play mode
        // Awake does not run again and private non-serialized fields come back null.
        void OnEnable()
        {
            _registry = GetComponent<SimWorldRegistry>();
            if (buildOnStart) StartBuild();
        }

        void OnDisable()
        {
            StopAll();
        }

        void OnApplicationQuit()
        {
            StopAll();
        }

        /// <summary>Discards everything and rebuilds the scene from scratch.</summary>
        public void Reload()
        {
            StopAll();
            StartBuild();
        }

        void StartBuild()
        {
            if (_run != null) return;
            _materials ??= new MaterialCache(materialTemplate);
            _materials.FlipTexturesVertically = flipTexturesVertically;
            _client ??= new ZmqSceneClient(host, requestPort, requestTimeoutMs);
            _run = StartCoroutine(Run());
        }

        void StopAll()
        {
            if (_run != null) { StopCoroutine(_run); _run = null; }
            _stream?.Dispose(); _stream = null;
            _client?.Dispose(); _client = null;
            ClearScene();
            State = BuildState.Idle;
        }

        void ClearScene()
        {
            if (_registry != null)
            {
                _registry.Clear();
                _registry.SceneVersion = -1;
            }
            if (_root != null) MaterialCache.Destroy(_root.gameObject);
            _root = null;
            foreach (var m in _meshes) MaterialCache.Destroy(m);
            _meshes.Clear();
            _failedTextures.Clear();
            _materials?.Dispose();
            _materials = null;
            ShapesTotal = ShapesBuilt = SubmeshCount = 0;
            VertexCount = TriangleCount = SourceCorners = 0;
        }

        void Update()
        {
            if (_stream == null) return;
            while (_stream.TryDequeueSceneEvent(out int v))
            {
                if (v != _registry.SceneVersion) _syncRequested = true;
            }
        }

        // ------------------------------------------------------------------ main flow

        IEnumerator Run()
        {
            while (true)
            {
                State = BuildState.Connecting;
                StatusMessage = $"connecting to tcp://{host}:{requestPort}...";
                var infoTask = _client.Request("info");
                yield return Wait(infoTask);
                if (!TryGet(infoTask, out var infoFrames, out var err))
                {
                    Fail($"no reply from the streamer: {err}");
                    yield return new WaitForSeconds(retryIntervalSeconds);
                    continue;
                }
                string infoError = null;
                try
                {
                    var json = FrameReader.Text(infoFrames[0]);
                    FrameReader.ThrowIfError(json);
                    Info = JsonUtility.FromJson<InfoReply>(json);
                }
                catch (Exception e)
                {
                    infoError = e.Message;
                }
                if (infoError != null)
                {
                    Fail($"invalid info: {infoError}");
                    yield return new WaitForSeconds(retryIntervalSeconds);
                    continue;
                }

                if (_stream == null)
                {
                    int port = posePort > 0 ? posePort : (Info.posePort > 0 ? Info.posePort : 23006);
                    _stream = new ZmqPoseStream(host, port);
                }
                if (_root == null)
                {
                    _root = new GameObject("CoppeliaScene").transform;
                    _root.SetParent(transform, false);
                    _registry.Root = _root;
                }
                _materials ??= new MaterialCache(materialTemplate) { FlipTexturesVertically = flipTexturesVertically };

                var sw = Stopwatch.StartNew();
                bool ok = false;
                yield return Sync(r => ok = r);
                if (!ok)
                {
                    yield return new WaitForSeconds(retryIntervalSeconds);
                    continue;
                }
                LastLoadSeconds = (float)sw.Elapsed.TotalSeconds;
                Debug.Log($"[SimWorld] Scene built in {LastLoadSeconds:F2} s: {ObjectCount} objects, " +
                          $"{ShapesBuilt} shapes, {SubmeshCount} submeshes, {VertexCount:N0} vertices, " +
                          $"{TriangleCount:N0} triangles, {_materials.MaterialCount} materials, " +
                          $"{_materials.TextureCount} textures, {_client.BytesReceived / 1e6:F1} MB received");

                // Wait for structural changes in the scene.
                while (true)
                {
                    if (_syncRequested)
                    {
                        _syncRequested = false;
                        sw.Restart();
                        yield return Sync(r => ok = r);
                        if (!ok) break; // go back to reconnecting
                        Debug.Log($"[SimWorld] Scene resynchronized (version {_registry.SceneVersion}) in {sw.Elapsed.TotalSeconds:F2} s");
                    }
                    yield return null;
                }
            }
        }

        void Fail(string msg)
        {
            State = BuildState.Error;
            StatusMessage = msg;
            Debug.LogWarning("[SimWorld] " + msg);
        }

        static IEnumerator Wait(Task t)
        {
            while (!t.IsCompleted) yield return null;
        }

        static bool TryGet<T>(Task<T> t, out T value, out string error)
        {
            value = default;
            error = null;
            if (t.IsCanceled) { error = "canceled"; return false; }
            if (t.IsFaulted)
            {
                var e = t.Exception?.GetBaseException();
                error = e?.Message ?? "error";
                return false;
            }
            value = t.Result;
            return true;
        }

        /// <summary>list -> applies the difference to the hierarchy -> builds the new meshes.</summary>
        IEnumerator Sync(Action<bool> done)
        {
            State = BuildState.Listing;
            StatusMessage = "list";
            var listTask = _client.Request("list");
            yield return Wait(listTask);
            if (!TryGet(listTask, out var frames, out var err)) { Fail("list: " + err); done(false); yield break; }

            ListReply list;
            try
            {
                var json = FrameReader.Text(frames[0]);
                FrameReader.ThrowIfError(json);
                list = JsonUtility.FromJson<ListReply>(json);
            }
            catch (Exception e) { Fail("invalid list: " + e.Message); done(false); yield break; }

            _registry.SetReady(false);
            var newShapes = ApplyHierarchy(list);
            _registry.SceneVersion = list.sceneVersion;

            yield return BuildMeshes(newShapes);
            State = BuildState.Ready;
            StatusMessage = $"ready (version {list.sceneVersion})";
            _registry.SetReady(true);
            done(true);
        }

        List<SimObject> ApplyHierarchy(ListReply list)
        {
            var objs = list.objects ?? Array.Empty<SceneObjectDto>();
            var seen = new HashSet<int>();
            var created = new List<SimObject>();
            var newShapes = new List<SimObject>();

            // 1) create the new ones, update name/path of the existing ones
            foreach (var d in objs)
            {
                seen.Add(d.h);
                if (_registry.TryGet(d.h, out var o))
                {
                    _registry.Rekey(o, d.path);
                    o.Name = d.name;
                    o.ParentHandle = d.parent;
                    o.VisibleInSim = d.visible;
                    if (o.Transform.name != d.name) o.Transform.name = d.name;
                    continue;
                }
                var go = new GameObject(string.IsNullOrEmpty(d.name) ? $"obj_{d.h}" : d.name);
                var r = go.AddComponent<SimObjectRef>();
                r.handle = d.h; r.path = d.path; r.simType = d.type;
                o = new SimObject
                {
                    Handle = d.h, Name = d.name, Path = d.path, Type = d.type, ParentHandle = d.parent,
                    IsShape = d.isShape, VisibleInSim = d.visible, Transform = go.transform,
                };
                created.Add(o);
                if (d.isShape) newShapes.Add(o);
                _registry.Register(o);
            }

            // 2) parents and local poses (list snapshot; the stream updates them later)
            foreach (var d in objs)
            {
                if (!_registry.TryGet(d.h, out var o)) continue;
                Transform parent = _root;
                if (d.parent != -1 && _registry.TryGet(d.parent, out var p)) parent = p.Transform;
                if (o.Transform.parent != parent) o.Transform.SetParent(parent, false);
                if (d.localPose != null && d.localPose.Length == 7)
                {
                    CoordinateConverter.PoseToUnity(d.localPose, 0, out var pos, out var rot);
                    o.Transform.SetLocalPositionAndRotation(pos, rot);
                }
                if (o.IsShape)
                {
                    var mr = o.Transform.GetComponent<MeshRenderer>();
                    if (mr != null) mr.enabled = !hideInvisibleShapes || o.VisibleInSim;
                }
            }

            // 3) remove the ones that are gone (surviving children were already reparented)
            var removed = new List<SimObject>();
            foreach (var kv in _registry.Objects) if (!seen.Contains(kv.Key)) removed.Add(kv.Value);
            foreach (var o in removed)
            {
                _registry.Unregister(o);
                if (o.IsShape)
                {
                    var mf = o.Transform.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        _meshes.Remove(mf.sharedMesh);
                        MaterialCache.Destroy(mf.sharedMesh);
                    }
                    ShapesBuilt--; ShapesTotal--;
                }
                MaterialCache.Destroy(o.Transform.gameObject);
            }
            if (removed.Count > 0 || created.Count > 0)
                Debug.Log($"[SimWorld] list v{list.sceneVersion}: +{created.Count} / -{removed.Count} objects");
            ShapesTotal += newShapes.Count;
            return newShapes;
        }

        IEnumerator BuildMeshes(List<SimObject> shapes)
        {
            State = BuildState.BuildingMeshes;
            var inflight = new Queue<(SimObject obj, Task<BuiltMesh> task)>();
            int next = 0;
            var frame = Stopwatch.StartNew();

            while (next < shapes.Count || inflight.Count > 0)
            {
                while (next < shapes.Count && inflight.Count < prefetch)
                {
                    var o = shapes[next++];
                    inflight.Enqueue((o, RequestAndBuild(o.Handle)));
                }
                var (obj, task) = inflight.Dequeue();
                StatusMessage = $"mesh {ShapesBuilt + 1}/{ShapesTotal}: {obj.Path}";
                while (!task.IsCompleted) { yield return null; frame.Restart(); }
                if (!TryGet(task, out var built, out var err))
                {
                    Debug.LogWarning($"[SimWorld] mesh of {obj.Path} failed: {err}");
                    ShapesBuilt++;
                    continue;
                }
                if (obj.Transform == null) { ShapesBuilt++; continue; } // removed meanwhile

                // textures that are not in the cache yet
                foreach (var k in built.Materials)
                {
                    if (k.Texture < 0 || _materials.HasTexture(k.Texture) || _failedTextures.Contains(k.Texture)) continue;
                    var tt = _client.Request("texture " + k.Texture);
                    while (!tt.IsCompleted) { yield return null; frame.Restart(); }
                    if (!TryGet(tt, out var tf, out var terr))
                    {
                        Debug.LogWarning($"[SimWorld] texture {k.Texture} failed: {terr}");
                        _failedTextures.Add(k.Texture);
                        continue;
                    }
                    try
                    {
                        var head = FrameReader.Text(tf[0]);
                        FrameReader.ThrowIfError(head);
                        _materials.AddTexture(JsonUtility.FromJson<TextureHeader>(head), tf[1], k.TexOptions);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[SimWorld] texture {k.Texture}: {e.Message}");
                        _failedTextures.Add(k.Texture);
                    }
                }

                ApplyMesh(obj, built);
                ShapesBuilt++;
                if (frame.Elapsed.TotalMilliseconds > frameBudgetMs)
                {
                    yield return null;
                    frame.Restart();
                }
            }
        }

        async Task<BuiltMesh> RequestAndBuild(int handle)
        {
            var frames = await _client.Request("mesh " + handle).ConfigureAwait(false);
            // decoding + welding off the main thread
            return ShapeMeshBuilder.Build(FrameReader.ParseMesh(frames));
        }

        void ApplyMesh(SimObject o, BuiltMesh b)
        {
            if (b.VertexCount == 0 || b.Submeshes.Count == 0) return;
            var mesh = b.ToMesh(o.Name);
            _meshes.Add(mesh);
            var go = o.Transform.gameObject;
            if (!go.TryGetComponent<MeshFilter>(out var mf)) mf = go.AddComponent<MeshFilter>();
            if (!go.TryGetComponent<MeshRenderer>(out var mr)) mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = mesh;
            var mats = new Material[b.Materials.Count];
            for (int i = 0; i < mats.Length; i++) mats[i] = _materials.Get(b.Materials[i]);
            mr.sharedMaterials = mats;
            mr.shadowCastingMode = castShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = castShadows;
            mr.enabled = !hideInvisibleShapes || o.VisibleInSim;
            if (addMeshColliders && o.VisibleInSim)
            {
                if (!go.TryGetComponent<MeshCollider>(out var mc)) mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
            }
            VertexCount += b.VertexCount;
            TriangleCount += b.TriangleCount;
            SourceCorners += b.SourceCorners;
            SubmeshCount += b.Submeshes.Count;
            _registry.RaiseMeshReady(o);
        }
    }
}
