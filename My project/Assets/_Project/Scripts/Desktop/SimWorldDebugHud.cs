using UnityEngine;
using VRRobot.SimWorld;
using VRRobot.SimWorld.Transport;

namespace VRRobot.Desktop
{
    /// <summary>Debug HUD (IMGUI): connection, build progress, counts and FPS.</summary>
    public class SimWorldDebugHud : MonoBehaviour
    {
        public CoppeliaSceneBuilder builder;
        public CoppeliaPoseSubscriber poses;
        public bool visible = true;

        float _fps, _fpsAccum;
        int _fpsFrames;
        GUIStyle _style;

        void OnEnable()
        {
            if (builder == null) builder = FindAnyObjectByType<CoppeliaSceneBuilder>();
            if (poses == null && builder != null) poses = builder.GetComponent<CoppeliaPoseSubscriber>();
        }

        void Update()
        {
            _fpsAccum += Time.unscaledDeltaTime;
            _fpsFrames++;
            if (_fpsAccum >= 0.5f)
            {
                _fps = _fpsFrames / _fpsAccum;
                _fpsAccum = 0;
                _fpsFrames = 0;
            }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f1Key.wasPressedThisFrame) visible = !visible;
        }

        void OnGUI()
        {
            if (!visible || builder == null) return;
            _style ??= new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
            GUILayout.BeginArea(new Rect(10, 10, 460, 330), GUI.skin.box);
            var b = builder;
            string color = b.State == CoppeliaSceneBuilder.BuildState.Ready ? "#7f7" :
                b.State == CoppeliaSceneBuilder.BuildState.Error ? "#f77" : "#ff7";
            GUILayout.Label($"<b>SimWorld</b>  <color={color}>{b.State}</color>  ({_fps:F0} fps)", _style);
            GUILayout.Label(b.StatusMessage, _style);
            if (b.Info != null)
                GUILayout.Label($"streamer: protocol {b.Info.protocol}, version {b.Info.sceneVersion}, " +
                                $"{b.Info.objectCount} objects / {b.Info.shapeCount} shapes", _style);
            GUILayout.Label($"shapes {b.ShapesBuilt}/{b.ShapesTotal}  objects {b.ObjectCount}  " +
                            $"submeshes {b.SubmeshCount}", _style);
            GUILayout.Label($"vertices {b.VertexCount:N0} (from {b.SourceCorners:N0} corners)  " +
                            $"triangles {b.TriangleCount:N0}", _style);
            if (b.Materials != null)
                GUILayout.Label($"materials {b.Materials.MaterialCount}  textures {b.Materials.TextureCount}  " +
                                $"received {(b.Client != null ? b.Client.BytesReceived / 1e6 : 0):F1} MB", _style);
            if (b.State == CoppeliaSceneBuilder.BuildState.Ready)
                GUILayout.Label($"load time: {b.LastLoadSeconds:F2} s", _style);
            var s = b.PoseStream;
            if (s != null)
            {
                double age = s.LastMessageTime < 0 ? -1 : ZmqPoseStream.Now - s.LastMessageTime;
                string ageTxt = age < 0 ? "<color=#f77>no messages</color>" :
                    age > 2 ? $"<color=#f77>last one {age:F1} s ago</color>" : "ok";
                GUILayout.Label($"stream 23006: {ageTxt}  msgs {s.MessagesReceived}  t_sim {s.LastSimTime:F2}", _style);
            }
            if (poses != null)
                GUILayout.Label($"poses applied: {poses.AppliedPerSecond:F0}/s  total {poses.TotalApplied}", _style);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reload scene")) b.Reload();
            GUILayout.Label("  F1: HUD | RMB+WASD/QE: fly", _style);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
