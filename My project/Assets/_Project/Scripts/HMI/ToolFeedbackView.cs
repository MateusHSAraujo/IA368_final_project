using System;
using System.Collections.Generic;
using UnityEngine;
using VRRobot.Core;

namespace VRRobot.HMI
{
    /// <summary>
    /// Procedural line renderers that show the tool feedback (plan, goal ring, active path, stroke preview, pending
    /// strokes) in a given space: the table (miniature) or the world. Shared by the table and the world overlay.
    /// </summary>
    internal sealed class ToolFeedbackView
    {
        const int CircleSegments = 32;

        readonly Func<Vector2, float, Vector3> _toLocal;
        readonly Func<float> _scale;
        readonly Transform _parent;
        readonly Material _planMat, _goalMat, _previewMat, _keepoutPreviewMat;
        readonly float _lineWidth, _height, _goalRadius;
        readonly LineRenderer _plan, _goal, _path, _preview;
        readonly List<LineRenderer> _pending = new List<LineRenderer>();
        readonly List<Vector3> _tmp = new List<Vector3>();

        /// <param name="toLocal">(mapXY, localHeight) to the local position inside <paramref name="parent"/>.</param>
        /// <param name="scale">Local units per world meter (k for the table, 1 for the world).</param>
        /// <param name="goalRadius">Goal ring radius in world meters.</param>
        public ToolFeedbackView(Transform parent, Func<Vector2, float, Vector3> toLocal, Func<float> scale,
            float lineWidth, float height, float goalRadius, Material plan, Material goal, Material preview,
            Material keepoutPreview)
        {
            _parent = parent; _toLocal = toLocal; _scale = scale;
            _lineWidth = lineWidth; _height = height; _goalRadius = goalRadius;
            _planMat = plan; _goalMat = goal; _previewMat = preview; _keepoutPreviewMat = keepoutPreview;
            _plan = Create("Plan", plan, lineWidth);
            _goal = Create("Goal", goal, lineWidth);
            _goal.loop = true;
            _path = Create("ActivePath", preview, lineWidth);
            _preview = Create("Preview", preview, lineWidth);
            _preview.numCapVertices = 8;
            _preview.numCornerVertices = 4;
        }

        LineRenderer Create(string name, Material mat, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.sharedMaterial = mat;
            lr.startWidth = lr.endWidth = width;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.alignment = LineAlignment.View;
            lr.numCornerVertices = 2;
            lr.enabled = false;
            return lr;
        }

        public void SetEnabled(bool on)
        {
            if (!on) { _plan.enabled = _goal.enabled = _path.enabled = _preview.enabled = false; HidePending(0); }
        }

        /// <summary>World points (any y) of the /plan path; shown only when <paramref name="visible"/>.</summary>
        public void SetPlan(IReadOnlyList<Vector3> world, bool visible)
        {
            if (!visible || world == null || world.Count < 2) { _plan.enabled = false; return; }
            _tmp.Clear();
            for (int i = 0; i < world.Count; i++) _tmp.Add(_toLocal(MapGridMath.WorldToMap(world[i]), _height));
            Apply(_plan, _tmp, _lineWidth);
        }

        public void Refresh(TacticalToolController tools)
        {
            if (tools == null) { _goal.enabled = _path.enabled = _preview.enabled = false; HidePending(0); return; }
            float s = _scale();

            if (tools.HasActiveGoal)
            {
                var c = _toLocal(tools.ActiveGoal, _height);
                float r = _goalRadius * s;
                _tmp.Clear();
                for (int i = 0; i < CircleSegments; i++)
                {
                    float a = i * Mathf.PI * 2f / CircleSegments;
                    _tmp.Add(c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
                }
                Apply(_goal, _tmp, _lineWidth);
            }
            else _goal.enabled = false;

            var ap = tools.ActivePath;
            if (ap != null && ap.Count >= 2) Apply(_path, ToLocal(ap), _lineWidth);
            else _path.enabled = false;

            var pp = tools.PreviewPoints;
            if (tools.PreviewKind == TacticalTool.Path && pp.Count >= 2)
            {
                _preview.sharedMaterial = _previewMat;
                Apply(_preview, ToLocal(pp), _lineWidth);
            }
            else if (tools.PreviewKind == TacticalTool.NoGo && pp.Count >= 1)
            {
                _preview.sharedMaterial = _keepoutPreviewMat;
                Apply(_preview, ToLocal(pp), BrushWidth(tools.BrushRadius, s));
            }
            else _preview.enabled = false;

            var pending = tools.PendingStrokes;
            for (int i = 0; i < pending.Count; i++)
            {
                if (i >= _pending.Count)
                {
                    var lr = Create("Pending" + i, _keepoutPreviewMat, _lineWidth);
                    lr.numCapVertices = 8;
                    lr.numCornerVertices = 4;
                    _pending.Add(lr);
                }
                Apply(_pending[i], ToLocal(pending[i].Points), BrushWidth(pending[i].Radius, s));
            }
            HidePending(pending.Count);
        }

        float BrushWidth(float radius, float scale) => Mathf.Max(2f * radius * scale, 0.002f);

        void HidePending(int from)
        {
            for (int i = from; i < _pending.Count; i++) _pending[i].enabled = false;
        }

        List<Vector3> ToLocal(IReadOnlyList<Vector2> pts)
        {
            _tmp.Clear();
            for (int i = 0; i < pts.Count; i++) _tmp.Add(_toLocal(pts[i], _height));
            return _tmp;
        }

        static void Apply(LineRenderer lr, List<Vector3> pts, float width)
        {
            if (pts.Count == 1) pts.Add(pts[0] + new Vector3(1e-4f, 0f, 0f)); // a single point draws a round dot
            lr.startWidth = lr.endWidth = width;
            lr.positionCount = pts.Count;
            lr.SetPositions(pts.ToArray());
            lr.enabled = true;
        }
    }
}
