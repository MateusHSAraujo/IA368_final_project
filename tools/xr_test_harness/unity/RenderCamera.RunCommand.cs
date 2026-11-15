// Renders a camera to a 1024x768 PNG in <temp>/xr_harness/render.png. Paste into Unity_RunCommand (Play mode).
// openxr_capture_composited_image can return a stale frame; this is the reliable alternative.
// Do not set stereoTargetEye (URP warns) and do not call Render() without a targetTexture.
using UnityEngine;
using UnityEditor;

internal class CommandScript : IRunCommand
{
    // --- Settings -------------------------------------------------------------------------
    const bool UseTempCamera = false;                       // false: render CenterEyeAnchor
    static readonly Vector3 CamPos = new Vector3(0f, 2f, -2f);
    static readonly Vector3 LookAt = new Vector3(0f, 1f, 0f);
    const float Fov = 60f;
    // --------------------------------------------------------------------------------------

    public void Execute(ExecutionResult result)
    {
        Camera cam;
        GameObject temp = null;
        if (UseTempCamera)
        {
            temp = new GameObject("TempRenderCamera");
            cam = temp.AddComponent<Camera>();
            cam.fieldOfView = Fov;
            cam.transform.position = CamPos;
            cam.transform.LookAt(LookAt);
        }
        else
        {
            var eye = GameObject.Find("CenterEyeAnchor");
            cam = eye != null ? eye.GetComponent<Camera>() : null;
            if (cam == null) { result.LogWarning("CenterEyeAnchor camera not found"); return; }
        }

        var rt = new RenderTexture(1024, 768, 24);
        var prevTarget = cam.targetTexture;
        var prevActive = RenderTexture.active;
        Texture2D tex = null;
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            tex = new Texture2D(1024, 768, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1024, 768), 0, 0);
            tex.Apply();
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xr_harness");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "render.png");
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            result.Log("saved " + path);
        }
        finally
        {
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            rt.Release();
            Object.DestroyImmediate(rt);
            if (tex != null) Object.DestroyImmediate(tex);
            if (temp != null) Object.DestroyImmediate(temp);
        }
    }
}
