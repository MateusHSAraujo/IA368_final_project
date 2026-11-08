using System.Collections.Generic;
using UnityEngine;
using VRRobot.Core;

namespace VRRobot.SimWorld
{
    /// <summary>
    /// Applies, on the main thread, the local poses received on port 23006. Only the latest pose
    /// of each handle is applied per frame. Raises <see cref="SimWorldRegistry.PosesApplied"/>.
    /// </summary>
    [RequireComponent(typeof(CoppeliaSceneBuilder))]
    public class CoppeliaPoseSubscriber : MonoBehaviour
    {
        CoppeliaSceneBuilder _builder;
        SimWorldRegistry _registry;
        readonly Dictionary<int, float[]> _batch = new Dictionary<int, float[]>();
        readonly List<int> _applied = new List<int>();

        public int AppliedLastFrame { get; private set; }
        public float AppliedPerSecond { get; private set; }
        public long TotalApplied { get; private set; }
        public double LastSimTime => _builder != null && _builder.PoseStream != null ? _builder.PoseStream.LastSimTime : 0;

        int _windowCount;
        float _windowStart;

        void OnEnable()
        {
            _builder = GetComponent<CoppeliaSceneBuilder>();
            _registry = GetComponent<SimWorldRegistry>();
        }

        void Update()
        {
            var stream = _builder.PoseStream;
            AppliedLastFrame = 0;
            if (stream != null)
            {
                _batch.Clear();
                stream.Drain(_batch);
                _applied.Clear();
                foreach (var kv in _batch)
                {
                    // Objects not created yet are skipped; the 1 s keyframe fixes them later.
                    if (!_registry.TryGet(kv.Key, out var o) || o.Transform == null) continue;
                    CoordinateConverter.PoseToUnity(kv.Value, 0, out var pos, out var rot);
                    o.Transform.SetLocalPositionAndRotation(pos, rot);
                    _applied.Add(kv.Key);
                }
                AppliedLastFrame = _applied.Count;
                TotalApplied += _applied.Count;
                _windowCount += _applied.Count;
                if (_applied.Count > 0) _registry.RaisePosesApplied(_applied);
            }
            float now = Time.unscaledTime;
            if (now - _windowStart >= 1f)
            {
                AppliedPerSecond = _windowCount / (now - _windowStart);
                _windowCount = 0;
                _windowStart = now;
            }
        }
    }
}
