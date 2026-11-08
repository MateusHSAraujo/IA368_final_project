using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRRobot.SimWorld
{
    /// <summary>A CoppeliaSim object rebuilt in Unity.</summary>
    public class SimObject
    {
        public int Handle;
        public string Name;
        /// <summary>Full CoppeliaSim path, e.g. "/house/kitchen/coffee_maker".</summary>
        public string Path;
        /// <summary>"shape", "dummy", "joint", ...</summary>
        public string Type;
        public int ParentHandle;
        public bool IsShape;
        public bool VisibleInSim;
        public Transform Transform;
        public GameObject GameObject => Transform != null ? Transform.gameObject : null;
    }

    /// <summary>
    /// EXTENSION POINT of Layer 1. VR (XR Origin), ROS and the tactical table depend only on this:
    /// look up objects by handle or path and react to build and pose events.
    /// All events are raised on the main thread.
    /// </summary>
    [DisallowMultipleComponent]
    public class SimWorldRegistry : MonoBehaviour
    {
        public static SimWorldRegistry Instance { get; private set; }

        readonly Dictionary<int, SimObject> _byHandle = new Dictionary<int, SimObject>();
        readonly Dictionary<string, SimObject> _byPath = new Dictionary<string, SimObject>(StringComparer.Ordinal);

        /// <summary>Root of the rebuilt scene (CoppeliaSim world frame, converted).</summary>
        public Transform Root { get; internal set; }

        /// <summary>true once the hierarchy and all meshes have been built.</summary>
        public bool IsReady { get; private set; }

        public int SceneVersion { get; internal set; } = -1;

        public IReadOnlyDictionary<int, SimObject> Objects => _byHandle;

        /// <summary>Full scene built (or rebuilt after a "scene" event).</summary>
        public event Action SceneBuilt;

        /// <summary>Object created (it already has a Transform and pose; the mesh may come later).</summary>
        public event Action<SimObject> ObjectSpawned;

        /// <summary>Object removed from CoppeliaSim; the GameObject is destroyed right after the event.</summary>
        public event Action<SimObject> ObjectRemoved;

        /// <summary>Shape mesh applied.</summary>
        public event Action<SimObject> MeshReady;

        /// <summary>Batch of poses applied this frame (changed handles).</summary>
        public event Action<IReadOnlyList<int>> PosesApplied;

        // OnEnable (not only Awake): after a domain reload in Play mode Awake does not run again.
        void OnEnable()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[SimWorld] More than one SimWorldRegistry in the scene; using the first one.", this);
                return;
            }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public bool TryGet(int handle, out SimObject obj) => _byHandle.TryGetValue(handle, out obj);

        public bool TryGetByPath(string path, out SimObject obj) => _byPath.TryGetValue(path, out obj);

        /// <summary>Shortcut: Transform by CoppeliaSim path, or null.</summary>
        public Transform Find(string path) => _byPath.TryGetValue(path, out var o) ? o.Transform : null;

        internal void Register(SimObject o)
        {
            _byHandle[o.Handle] = o;
            if (!string.IsNullOrEmpty(o.Path)) _byPath[o.Path] = o;
            ObjectSpawned?.Invoke(o);
        }

        internal void Unregister(SimObject o)
        {
            _byHandle.Remove(o.Handle);
            if (!string.IsNullOrEmpty(o.Path) && _byPath.TryGetValue(o.Path, out var cur) && cur == o)
                _byPath.Remove(o.Path);
            ObjectRemoved?.Invoke(o);
        }

        internal void Rekey(SimObject o, string newPath)
        {
            if (o.Path == newPath) return;
            if (!string.IsNullOrEmpty(o.Path) && _byPath.TryGetValue(o.Path, out var cur) && cur == o)
                _byPath.Remove(o.Path);
            o.Path = newPath;
            if (!string.IsNullOrEmpty(newPath)) _byPath[newPath] = o;
        }

        internal void SetReady(bool ready)
        {
            IsReady = ready;
            if (ready) SceneBuilt?.Invoke();
        }

        internal void RaiseMeshReady(SimObject o) => MeshReady?.Invoke(o);

        internal void RaisePosesApplied(IReadOnlyList<int> handles) => PosesApplied?.Invoke(handles);

        internal void Clear()
        {
            _byHandle.Clear();
            _byPath.Clear();
            IsReady = false;
        }
    }
}
