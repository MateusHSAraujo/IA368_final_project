// Unity-side helper for aim_tools.py. Paste into Unity_RunCommand while in Play mode.
//
// Re-paste after EVERY domain reload (entering Play mode reloads it, and so does any C# edit).
// A second paste adds a second delegate: harmless but redundant.
//
// What it does (static EditorApplication.update delegate):
//  a) QueuePlayerLoopUpdate() while playing and not paused (Play mode keeps ticking with the Editor unfocused);
//  b) every 0.1 s reads <temp>/xr_harness/aim_target.txt (a spec, see README) and resolves the target point;
//  c) finds the right controller ray origin (Oculus types cannot be referenced directly here and
//     System.Reflection is blocked, so it uses Type.GetType + SerializedObject);
//  d) writes <temp>/xr_harness/aim_state.json with the OpenXR delta rotation (dxr) that aims the ray at the target.
using UnityEngine;
using UnityEditor;

internal class CommandScript : IRunCommand
{
    static double _next;
    static Component _ray;
    static Transform _origin, _ts, _eye;
    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    static string Dir => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xr_harness");

    public void Execute(ExecutionResult result)
    {
        System.IO.Directory.CreateDirectory(Dir);
        EditorApplication.update += Tick;
        result.Log("[AimHelper] delegate added; exchange dir " + Dir);
    }

    static float F(string s) { return float.Parse(s, Inv); }

    static Vector3? Resolve(string spec)
    {
        int i = spec.IndexOf(':');
        if (i < 0) return null;
        string kind = spec.Substring(0, i), arg = spec.Substring(i + 1);
        string[] p = arg.Split(';');
        switch (kind)
        {
            case "button":
                // Any Selectable (Button or Toggle: the UI Set tabs, tools and brush are toggles); aim at the rect centre.
                foreach (var b in Object.FindObjectsByType<UnityEngine.UI.Selectable>(FindObjectsSortMode.None))
                    if (b.gameObject.activeInHierarchy && b.gameObject.name == arg)
                    {
                        var rt = b.transform as RectTransform;
                        return rt != null ? rt.TransformPoint(rt.rect.center) : b.transform.position;
                    }
                return null;
            case "map":
            {
                var t = Object.FindFirstObjectByType<VRRobot.HMI.OperationsPanel>();
                if (t == null || !t.IsOpen) return null;  // panel closed: not found
                return t.MapToPanelWorld(new Vector2(F(p[0]), F(p[1])), 0f);
            }
            case "floor":
                return VRRobot.Core.MapGridMath.MapToWorld(new Vector2(F(p[0]), F(p[1])), 0f);
            case "world":
                return new Vector3(F(p[0]), F(p[1]), F(p[2]));
            case "handle":
            {
                // Corner resize handle: 0 near-left, 1 near-right, 2 far-left, 3 far-right.
                var panel = Object.FindFirstObjectByType<VRRobot.HMI.OperationsPanel>();
                var hs = panel != null ? panel.transform.Find("Pivot/Handles") : null;
                int idx = int.Parse(p[0], Inv);
                if (hs == null || !hs.gameObject.activeInHierarchy || idx >= hs.childCount) return null;
                return hs.GetChild(idx).position;
            }
            case "bar":
            {
                var panel = Object.FindFirstObjectByType<VRRobot.HMI.OperationsPanel>();
                if (panel == null || _eye == null) return null;
                var frame = panel.transform.Find("Pivot/Content/Frame");
                if (frame == null) return null;
                Transform best = null; float bd = float.MaxValue;
                foreach (Transform c in frame)
                {
                    float d = (c.position - _eye.position).sqrMagnitude;
                    if (d < bd) { bd = d; best = c; }
                }
                if (best == null) return null;
                return best.position + panel.transform.right * F(p[0]) + Vector3.up * F(p[1]);
            }
        }
        return null;
    }

    static string Pointer()
    {
        var panel = Object.FindFirstObjectByType<VRRobot.HMI.OperationsPanel>();
        var ptr = Object.FindFirstObjectByType<VRRobot.HMI.HmiPointer>();
        string s = ptr != null
            ? ptr.Kind + " valid=" + ptr.Valid + " map=" + ptr.MapPoint + " trigger=" + ptr.TriggerHeld
            : "no HmiPointer";
        s += " panel=" + (panel != null && panel.IsOpen ? "open" : "closed");
        if (panel != null)
        {
            var t = panel.transform;
            s += " tpos=" + t.position.ToString("F2") + " tyaw=" + t.eulerAngles.y.ToString("F1", Inv)
                 + " tpitch=" + t.eulerAngles.x.ToString("F1", Inv) + " troll=" + t.eulerAngles.z.ToString("F1", Inv)
                 + " tscale=" + t.lossyScale.x.ToString("F2", Inv);
        }
        var rig = GameObject.Find("[BuildingBlock] Camera Rig");
        if (rig != null) s += " rig=" + rig.transform.position.ToString("F2");
        return s;
    }

    static void Tick()
    {
        if (EditorApplication.isPlaying && !EditorApplication.isPaused) EditorApplication.QueuePlayerLoopUpdate();
        if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup < _next) return;
        _next = EditorApplication.timeSinceStartup + 0.1;
        try
        {
            string file = System.IO.Path.Combine(Dir, "aim_target.txt");
            if (!System.IO.File.Exists(file)) return;
            string spec = System.IO.File.ReadAllText(file).Trim();
            if (spec.Length == 0) return;

            if (_ts == null) _ts = GameObject.Find("TrackingSpace")?.transform;
            if (_eye == null) _eye = GameObject.Find("CenterEyeAnchor")?.transform;
            if (_origin == null)
            {
                var root = GameObject.Find("ComprehensiveInteractorsRight");
                var type = System.Type.GetType("Oculus.Interaction.RayInteractor, Oculus.Interaction");
                if (root != null && type != null) _ray = root.GetComponentInChildren(type);
                if (_ray != null)
                    _origin = (Transform)new SerializedObject(_ray).FindProperty("_rayOrigin").objectReferenceValue;
            }
            if (_origin == null || _ts == null) return;

            Vector3? target = Resolve(spec);
            float angle = 0f;
            float[] dxr = { 0f, 0f, 0f, 1f };
            if (target.HasValue)
            {
                var from = Quaternion.FromToRotation(_origin.forward, (target.Value - _origin.position).normalized);
                var du = Quaternion.Inverse(_ts.rotation) * from * _ts.rotation;
                angle = Quaternion.Angle(Quaternion.identity, from);
                dxr = new[] { -du.x, -du.y, du.z, du.w };
            }
            string json = "{\"t\": " + (System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0).ToString("F3", Inv)
                + ", \"spec\": \"" + spec.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
                + ", \"angle\": " + angle.ToString("F3", Inv)
                + ", \"dxr\": [" + string.Join(", ", System.Array.ConvertAll(dxr, v => v.ToString("F6", Inv))) + "]"
                + ", \"pointer\": \"" + Pointer().Replace("\"", "'") + "\"}";
            System.IO.File.WriteAllText(System.IO.Path.Combine(Dir, "aim_state.json"), json);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[AimHelper] " + e.Message);
            _ray = null; _origin = null; _ts = null; _eye = null;
        }
    }
}
