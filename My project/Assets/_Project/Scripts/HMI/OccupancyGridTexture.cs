using System;
using UnityEngine;
using VRRobot.Core;

namespace VRRobot.HMI
{
    /// <summary>
    /// Renders an occupancy grid (or a keepout mask) into a reused point-filtered texture. Row 0 of the grid is the
    /// bottom row of the texture.
    /// </summary>
    public class OccupancyGridTexture : IDisposable
    {
        readonly bool _isMask;
        readonly Color32 _a, _b, _c;
        readonly int _threshold;
        Color32[] _buffer;

        public Texture2D Texture { get; private set; }
        public GridInfo Info { get; private set; }

        public OccupancyGridTexture(Color32 unknown, Color32 free, Color32 occupied, int occupiedThreshold = 65)
        {
            _a = unknown; _b = free; _c = occupied; _threshold = occupiedThreshold;
        }

        OccupancyGridTexture(Color32 on, Color32 off, int threshold)
        {
            _isMask = true; _a = on; _b = off; _threshold = threshold;
        }

        /// <summary>Texture for a mask: values at or above <paramref name="threshold"/> use <paramref name="on"/>.</summary>
        public static OccupancyGridTexture CreateMask(Color32 on, Color32 off, int threshold = 50) =>
            new OccupancyGridTexture(on, off, threshold);

        /// <summary>Fills the texture; returns true when its size changed (or it was created).</summary>
        public bool Apply(sbyte[] data, GridInfo info)
        {
            if (!info.IsValid || data == null || data.Length != info.Width * info.Height) return false;

            bool resized = Texture == null || Texture.width != info.Width || Texture.height != info.Height;
            if (resized)
            {
                DestroyTexture();
                Texture = new Texture2D(info.Width, info.Height, TextureFormat.RGBA32, false)
                {
                    name = _isMask ? "KeepoutMaskTexture" : "OccupancyGridTexture",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _buffer = new Color32[data.Length];
            }

            if (_isMask) MapGridMath.MaskToColors(data, _buffer, _a, _b, _threshold);
            else MapGridMath.OccupancyToColors(data, _buffer, _a, _b, _c, _threshold);
            Texture.SetPixels32(_buffer);
            Texture.Apply(false);
            Info = info;
            return resized;
        }

        public void Dispose()
        {
            DestroyTexture();
            _buffer = null;
        }

        void DestroyTexture()
        {
            if (Texture == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(Texture);
            else UnityEngine.Object.DestroyImmediate(Texture);
            Texture = null;
        }
    }
}
