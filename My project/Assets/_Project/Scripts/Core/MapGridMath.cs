using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRRobot.Core
{
    /// <summary>Geometry of a 2D occupancy grid in the ROS <c>map</c> frame (origin yaw assumed 0).</summary>
    public struct GridInfo
    {
        public int Width, Height;
        public float Resolution;
        /// <summary>ROS xy of the outer corner of cell (0,0).</summary>
        public Vector2 Origin;

        public bool IsValid => Width > 0 && Height > 0 && Resolution > 0f;

        /// <summary>Grid extent in meters: (Width * Resolution, Height * Resolution).</summary>
        public Vector2 Size => new Vector2(Width * Resolution, Height * Resolution);
    }

    /// <summary>Pure math for occupancy grids, map/world/table conversions and stroke sampling.</summary>
    public static class MapGridMath
    {
        public static Vector3 MapToWorld(Vector2 mapXY, float height = 0f) =>
            CoordinateConverter.PositionToUnity(mapXY.x, mapXY.y, height);

        public static Vector2 WorldToMap(Vector3 world)
        {
            var r = CoordinateConverter.PositionFromUnity(world);
            return new Vector2(r.x, r.y);
        }

        public static bool Contains(GridInfo g, Vector2 mapXY)
        {
            if (!g.IsValid) return false;
            var s = g.Size;
            float dx = mapXY.x - g.Origin.x, dy = mapXY.y - g.Origin.y;
            return dx >= 0f && dy >= 0f && dx < s.x && dy < s.y;
        }

        public static bool TryGetCell(GridInfo g, Vector2 mapXY, out int col, out int row)
        {
            col = row = -1;
            if (!Contains(g, mapXY)) return false;
            col = Mathf.Min(g.Width - 1, Mathf.FloorToInt((mapXY.x - g.Origin.x) / g.Resolution));
            row = Mathf.Min(g.Height - 1, Mathf.FloorToInt((mapXY.y - g.Origin.y) / g.Resolution));
            return true;
        }

        public static int CellIndex(GridInfo g, int col, int row) => row * g.Width + col;

        public static Vector2 CellCenter(GridInfo g, int col, int row) =>
            new Vector2(g.Origin.x + (col + 0.5f) * g.Resolution, g.Origin.y + (row + 0.5f) * g.Resolution);

        /// <summary>ROS xy of the grid center.</summary>
        public static Vector2 Center(GridInfo g) => g.Origin + g.Size * 0.5f;

        public static Vector3 WorldCenter(GridInfo g, float height = 0f) => MapToWorld(Center(g), height);

        /// <summary>Unity world extent (x, z) of the grid: Unity x = -y_ros, Unity z = x_ros.</summary>
        public static Vector2 WorldSizeXZ(GridInfo g) => new Vector2(g.Height * g.Resolution, g.Width * g.Resolution);

        public static Vector3 WorldToTableLocal(Vector3 world, Vector3 worldCenter, float scale) =>
            (world - worldCenter) * scale;

        /// <param name="scale">Must be greater than zero.</param>
        public static Vector3 TableLocalToWorld(Vector3 local, Vector3 worldCenter, float scale) =>
            local / scale + worldCenter;

        /// <summary>
        /// Classifies occupancy values (-1 unknown, 0..100). The output layout equals the data layout (row 0 first),
        /// which matches <c>Texture2D.SetPixels32</c> (row 0 = bottom).
        /// </summary>
        public static void OccupancyToColors(sbyte[] data, Color32[] output, Color32 unknown, Color32 free,
            Color32 occupied, int occupiedThreshold = 65)
        {
            CheckLengths(data, output);
            for (int i = 0; i < data.Length; i++)
            {
                int v = data[i];
                output[i] = v < 0 ? unknown : v >= occupiedThreshold ? occupied : free;
            }
        }

        /// <summary>Maps a mask (value >= threshold) to <paramref name="on"/>, everything else to <paramref name="off"/>.</summary>
        public static void MaskToColors(sbyte[] data, Color32[] output, Color32 on, Color32 off, int threshold = 50)
        {
            CheckLengths(data, output);
            for (int i = 0; i < data.Length; i++) output[i] = data[i] >= threshold ? on : off;
        }

        /// <summary>True when the point is inside the grid and its cell is known and below the occupied threshold.</summary>
        public static bool IsFreeCell(sbyte[] data, GridInfo g, Vector2 mapXY, int occupiedThreshold = 65)
        {
            if (data == null || !TryGetCell(g, mapXY, out int col, out int row)) return false;
            int i = CellIndex(g, col, row);
            if (i >= data.Length) return false;
            int v = data[i];
            return v >= 0 && v < occupiedThreshold;
        }

        /// <summary>
        /// Keeps the first point, then every point at least <paramref name="minSpacing"/> from the last kept one, and
        /// appends the last input point when it differs from the last kept one by more than 1e-4. When the result
        /// exceeds <paramref name="maxPoints"/> it is decimated uniformly keeping both endpoints.
        /// </summary>
        public static List<Vector2> Resample(IList<Vector2> points, float minSpacing, int maxPoints)
        {
            var result = new List<Vector2>();
            if (points == null || points.Count == 0) return result;
            result.Add(points[0]);
            for (int i = 1; i < points.Count; i++)
                if (Vector2.Distance(points[i], result[result.Count - 1]) >= minSpacing) result.Add(points[i]);
            var last = points[points.Count - 1];
            if (Vector2.Distance(last, result[result.Count - 1]) > 1e-4f) result.Add(last);

            if (maxPoints >= 2 && result.Count > maxPoints)
            {
                var decimated = new List<Vector2>(maxPoints);
                for (int k = 0; k < maxPoints; k++)
                    decimated.Add(result[Mathf.RoundToInt(k * (result.Count - 1) / (float)(maxPoints - 1))]);
                return decimated;
            }
            return result;
        }

        public static float PathLength(IList<Vector2> points)
        {
            float length = 0f;
            if (points == null) return length;
            for (int i = 1; i < points.Count; i++) length += Vector2.Distance(points[i - 1], points[i]);
            return length;
        }

        /// <summary>Adds <paramref name="p"/> when it is at least <paramref name="minSpacing"/> from the last point (the first is always added).</summary>
        public static bool AppendIfFar(List<Vector2> points, Vector2 p, float minSpacing)
        {
            if (points.Count > 0 && Vector2.Distance(points[points.Count - 1], p) < minSpacing) return false;
            points.Add(p);
            return true;
        }

        static void CheckLengths(sbyte[] data, Color32[] output)
        {
            if (data == null || output == null || data.Length != output.Length)
                throw new ArgumentException("Data and output lengths must match.");
        }
    }
}
