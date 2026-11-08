using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VRRobot.SimWorld.Protocol;

namespace VRRobot.SimWorld.Building
{
    /// <summary>
    /// URP Lit materials shared per <see cref="MaterialKey"/>, and textures per CoppeliaSim id.
    /// Mapping: diffuse -> _BaseColor; modulate texture -> color x texture; decal -> texture only;
    /// transparency -> Transparent surface (alpha blend); no backface culling -> Render Face Both.
    /// </summary>
    public class MaterialCache : IDisposable
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
        static readonly int Metallic = Shader.PropertyToID("_Metallic");
        static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        static readonly int Cull = Shader.PropertyToID("_Cull");
        static readonly int Surface = Shader.PropertyToID("_Surface");
        static readonly int Blend = Shader.PropertyToID("_Blend");
        static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
        static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
        static readonly int SrcBlendAlpha = Shader.PropertyToID("_SrcBlendAlpha");
        static readonly int DstBlendAlpha = Shader.PropertyToID("_DstBlendAlpha");
        static readonly int ZWrite = Shader.PropertyToID("_ZWrite");

        readonly Shader _shader;
        readonly Dictionary<MaterialKey, Material> _materials = new Dictionary<MaterialKey, Material>();
        readonly Dictionary<int, Texture2D> _textures = new Dictionary<int, Texture2D>();

        public int MaterialCount => _materials.Count;
        public int TextureCount => _textures.Count;
        public bool FlipTexturesVertically { get; set; }

        public MaterialCache(Material template = null)
        {
            _shader = template != null ? template.shader : Shader.Find("Universal Render Pipeline/Lit");
            if (_shader == null) throw new InvalidOperationException("Shader 'Universal Render Pipeline/Lit' not found");
        }

        public bool HasTexture(int id) => _textures.ContainsKey(id);

        public void AddTexture(TextureHeader h, byte[] rgba, int texOptions)
        {
            if (_textures.ContainsKey(h.id)) return;
            if (rgba.Length != h.w * h.h * 4)
                throw new SimProtocolException($"texture {h.id}: {rgba.Length} bytes for {h.w}x{h.h}");
            if (FlipTexturesVertically) rgba = FlipRows(rgba, h.w, h.h);
            var t = new Texture2D(h.w, h.h, TextureFormat.RGBA32, true)
            {
                name = $"coppelia_tex_{h.id}",
                wrapModeU = (texOptions & 1) != 0 ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                wrapModeV = (texOptions & 2) != 0 ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                filterMode = (texOptions & 4) != 0 ? FilterMode.Trilinear : FilterMode.Point,
                anisoLevel = 4,
            };
            t.SetPixelData(rgba, 0); // level 0 only; Apply(true) generates the mips
            t.Apply(true, true);
            _textures[h.id] = t;
        }

        static byte[] FlipRows(byte[] src, int w, int h)
        {
            var dst = new byte[src.Length];
            int row = w * 4;
            for (int y = 0; y < h; y++) Buffer.BlockCopy(src, y * row, dst, (h - 1 - y) * row, row);
            return dst;
        }

        public Material Get(MaterialKey k)
        {
            if (_materials.TryGetValue(k, out var m)) return m;
            m = new Material(_shader) { name = Describe(k) };

            Color baseColor = (Color)k.Diffuse;
            if (k.Texture >= 0 && _textures.TryGetValue(k.Texture, out var tex))
            {
                m.SetTexture(BaseMap, tex);
                if (k.TexApplyMode == 1) baseColor = Color.white; // decal: the texture replaces the color
            }
            baseColor.a = k.Alpha / 255f;
            m.SetColor(BaseColor, baseColor);
            m.SetFloat(Metallic, 0f);
            Color spec = k.Specular;
            m.SetFloat(Smoothness, Mathf.Clamp(0.15f + 0.5f * spec.grayscale, 0f, 0.8f));

            Color em = k.Emission;
            if (em.maxColorComponent > 0.01f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor(EmissionColor, em);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            m.SetFloat(Cull, k.BackfaceCulling ? (float)CullMode.Back : (float)CullMode.Off);
            m.doubleSidedGI = !k.BackfaceCulling;

            if (k.Transparent)
            {
                m.SetFloat(Surface, 1f);
                m.SetFloat(Blend, 0f);
                m.SetFloat(SrcBlend, (float)BlendMode.SrcAlpha);
                m.SetFloat(DstBlend, (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat(SrcBlendAlpha, (float)BlendMode.One);
                m.SetFloat(DstBlendAlpha, (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat(ZWrite, 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetOverrideTag("RenderType", "Transparent");
                m.renderQueue = (int)RenderQueue.Transparent;
            }
            else
            {
                m.SetFloat(Surface, 0f);
                m.SetFloat(ZWrite, 1f);
                m.renderQueue = (int)RenderQueue.Geometry;
            }
            _materials[k] = m;
            return m;
        }

        static string Describe(MaterialKey k) =>
            $"coppelia_{ColorUtility.ToHtmlStringRGB(k.Diffuse)}" +
            (k.Texture >= 0 ? $"_t{k.Texture}" : "") + (k.Transparent ? $"_a{k.Alpha}" : "");

        public void Dispose()
        {
            foreach (var m in _materials.Values) Destroy(m);
            foreach (var t in _textures.Values) Destroy(t);
            _materials.Clear();
            _textures.Clear();
        }

        internal static void Destroy(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
