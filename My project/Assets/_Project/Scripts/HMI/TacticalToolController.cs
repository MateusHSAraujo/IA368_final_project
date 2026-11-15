using System;
using System.Collections.Generic;
using System.Globalization;
using RosMessageTypes.Nav;
using UnityEngine;
using VRRobot.Core;
using VRRobot.XR;

namespace VRRobot.HMI
{
    public enum TacticalTool { None, Goal, Path, NoGo }

    /// <summary>A keepout stroke already sent, drawn until the real mask arrives.</summary>
    public sealed class PendingStroke
    {
        public IReadOnlyList<Vector2> Points;
        public float Radius;
        public float SentTime;
    }

    /// <summary>
    /// Interprets the <see cref="HmiPointer"/> according to the active tool (goal, path painting, no-go brush) and
    /// sends the result through <see cref="RosHmiClient"/>. Exposes the state the renderers need.
    /// </summary>
    public class TacticalToolController : MonoBehaviour
    {
        const string LogPrefix = "[HMI] ";

        [SerializeField] HmiPointer _pointer;
        [SerializeField] LocomotionInputGate _gate;
        [SerializeField] float[] _brushRadii = { 0.15f, 0.30f, 0.50f };
        [SerializeField] float _pathSpacing = 0.05f;
        [SerializeField] int _maxPoints = 500;
        [SerializeField] float _minPathLength = 0.3f;

        public TacticalTool Tool { get; private set; }
        public event Action<TacticalTool> ToolChanged;

        public int BrushIndex { get; private set; }
        public float BrushRadius => _brushRadii != null && _brushRadii.Length > 0
            ? _brushRadii[Mathf.Clamp(BrushIndex, 0, _brushRadii.Length - 1)] : 0.3f;
        public int BrushCount => _brushRadii != null ? _brushRadii.Length : 0;
        public float BrushRadiusAt(int index) =>
            BrushCount > 0 ? _brushRadii[Mathf.Clamp(index, 0, BrushCount - 1)] : 0.3f;
        public event Action BrushChanged;

        public IReadOnlyList<Vector2> PreviewPoints => _stroke;
        public TacticalTool PreviewKind => _stroking ? Tool : TacticalTool.None;
        public IReadOnlyList<PendingStroke> PendingStrokes => _pending;
        public bool HasActiveGoal { get; private set; }
        public Vector2 ActiveGoal { get; private set; }
        public IReadOnlyList<Vector2> ActivePath => _activePath;
        public event Action PreviewChanged;

        readonly List<Vector2> _stroke = new List<Vector2>();
        readonly List<PendingStroke> _pending = new List<PendingStroke>();
        List<Vector2> _activePath = new List<Vector2>();
        bool _stroking, _goalArmed, _dirty, _subscribed, _sawActive;
        float _sentTime;
        string _sentKind;

        void OnEnable() => TrySubscribe();

        void Start() => TrySubscribe();

        void OnDisable()
        {
            CancelStroke();
            if (_pointer != null) _pointer.Active = false;
            var c = RosHmiClient.Instance;
            if (_subscribed && c != null)
            {
                c.MissionStateChanged -= OnMission;
                c.KeepoutMaskReceived -= OnKeepoutMask;
            }
            _subscribed = false;
        }

        void TrySubscribe()
        {
            var c = RosHmiClient.Instance;
            if (_subscribed || c == null) return;
            c.MissionStateChanged += OnMission;
            c.KeepoutMaskReceived += OnKeepoutMask;
            _subscribed = true;
        }

        public void SetTool(TacticalTool t)
        {
            if (t == Tool) return;
            CancelStroke();
            _goalArmed = false;
            Tool = t;
            Debug.Log(LogPrefix + "tool " + t);
            ToolChanged?.Invoke(t);
            _dirty = true;
        }

        public void SetBrush(int index)
        {
            if (BrushCount == 0) return;
            index = Mathf.Clamp(index, 0, BrushCount - 1);
            if (index == BrushIndex) return;
            BrushIndex = index;
            Debug.Log(LogPrefix + "brush radius " + BrushRadius.ToString("F2", CultureInfo.InvariantCulture) + " m");
            BrushChanged?.Invoke();
        }

        void LateUpdate()
        {
            TrySubscribe();
            ExpirePending();

            if (_pointer != null)
            {
                _pointer.Active = Tool != TacticalTool.None;
                if (!_pointer.Operative) { CancelStroke(); _goalArmed = false; }
                else Process();
            }

            if (_dirty)
            {
                _dirty = false;
                PreviewChanged?.Invoke();
            }
        }

        void Process()
        {
            bool valid = IsValid(_pointer);
            _pointer.SetValidity(valid);
            var p = _pointer.MapPoint;

            switch (Tool)
            {
                case TacticalTool.Goal:
                    if (_pointer.TriggerDown) _goalArmed = valid;
                    if (_pointer.TriggerUp)
                    {
                        if (_goalArmed && valid) SendGoal(p);
                        _goalArmed = false;
                    }
                    break;

                case TacticalTool.Path:
                case TacticalTool.NoGo:
                    if (_pointer.TriggerDown && valid) BeginStroke(p);
                    else if (_stroking && _pointer.TriggerHeld && valid) ExtendStroke(p);
                    if (_stroking && _pointer.TriggerUp) FinishStroke();
                    break;
            }
        }

        bool IsValid(HmiPointer ptr)
        {
            var c = RosHmiClient.Instance;
            if (c == null || c.LastMap == null || !c.LastMapInfo.IsValid) return false;
            if (!ptr.HasPoint) return false;
            var p = ptr.MapPoint;
            return Tool == TacticalTool.Goal
                ? MapGridMath.IsFreeCell(c.LastMap.data, c.LastMapInfo, p)
                : MapGridMath.Contains(c.LastMapInfo, p);
        }

        // ---- Strokes ---------------------------------------------------------------------------------------------

        float Spacing => Tool == TacticalTool.NoGo ? BrushRadius * 0.5f : _pathSpacing;

        void BeginStroke(Vector2 p)
        {
            _stroke.Clear();
            _stroking = true;
            if (_gate != null) _gate.Acquire(this);
            MapGridMath.AppendIfFar(_stroke, p, Spacing);
            _dirty = true;
        }

        void ExtendStroke(Vector2 p)
        {
            if (_stroke.Count >= _maxPoints) return;
            if (MapGridMath.AppendIfFar(_stroke, p, Spacing)) _dirty = true;
        }

        void FinishStroke()
        {
            var points = new List<Vector2>(_stroke);
            var kind = Tool;
            EndStroke();
            var c = RosHmiClient.Instance;
            if (c == null || points.Count == 0) return;

            if (kind == TacticalTool.Path)
            {
                float len = MapGridMath.PathLength(points);
                if (len < _minPathLength)
                {
                    Debug.Log(LogPrefix + "path too short (" + len.ToString("F2", CultureInfo.InvariantCulture) + " m), discarded");
                    return;
                }
                if (!IsConnected("path")) return;
                var sent = MapGridMath.Resample(points, _pathSpacing, _maxPoints);
                c.SendPath(sent);
                _activePath = sent;
                ArmMissionTracking("path");
                HasActiveGoal = false;
            }
            else if (kind == TacticalTool.NoGo)
            {
                if (!IsConnected("no-go stroke")) return;
                float r = BrushRadius;
                c.SendStroke(points, r);
                _pending.Add(new PendingStroke { Points = points, Radius = r, SentTime = Time.time });
            }
            _dirty = true;
        }

        void SendGoal(Vector2 p)
        {
            var c = RosHmiClient.Instance;
            if (c == null || !IsConnected("goal")) return;
            c.SendGoal(p);
            ActiveGoal = p;
            HasActiveGoal = true;
            _activePath = new List<Vector2>();
            ArmMissionTracking("goal");
            _dirty = true;
        }

        static bool IsConnected(string what)
        {
            var c = RosHmiClient.Instance;
            if (c != null && c.IsConnected) return true;
            Debug.LogWarning(LogPrefix + "not connected to ROS: " + what + " not sent");
            return false;
        }

        void ArmMissionTracking(string kind)
        {
            _sentTime = Time.time;
            _sawActive = false;
            _sentKind = kind;
        }

        /// <summary>Drops the pending no-go previews (after the keepout layer was cleared).</summary>
        public void ClearPending()
        {
            _pending.Clear();
            _dirty = true;
        }

        void EndStroke()
        {
            if (_gate != null) _gate.Release(this);
            _stroking = false;
            _stroke.Clear();
            _dirty = true;
        }

        void CancelStroke()
        {
            if (!_stroking) return;
            EndStroke();
        }

        // ---- ROS feedback ----------------------------------------------------------------------------------------

        void OnMission(MissionState s)
        {
            if (s == null || (!HasActiveGoal && _activePath.Count == 0)) return;
            if (s.IsActive)
            {
                // A stale active state of the previous mission (other mode) can arrive right after sending.
                if (Time.time - _sentTime < 1.5f && s.mode != _sentKind) return;
                _sawActive = true;
                if (s.mode == "path" && HasActiveGoal) { HasActiveGoal = false; _dirty = true; }
                else if (s.mode == "goal" && _activePath.Count > 0) { _activePath = new List<Vector2>(); _dirty = true; }
                return;
            }
            bool terminal = s.state == "SUCCEEDED" || s.state == "FAILED" || s.state == "CANCELED" || s.state == "IDLE";
            // Ignore a stale terminal state published right before the supervisor reacted to the command.
            if (!terminal || (!_sawActive && Time.time - _sentTime < 1.5f)) return;
            HasActiveGoal = false;
            _activePath = new List<Vector2>();
            _dirty = true;
        }

        void OnKeepoutMask(OccupancyGridMsg msg, GridInfo info)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (Time.time - _pending[i].SentTime >= 0.3f) { _pending.RemoveAt(i); _dirty = true; }
        }

        void ExpirePending()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (Time.time - _pending[i].SentTime >= 4f) { _pending.RemoveAt(i); _dirty = true; }
        }
    }
}
