using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VRRobot.Core;

namespace VRRobot.Tests
{
    public class MapGridMathTests
    {
        // Real map numbers: 310x210 cells, 0.05 m, origin (-1.52, -3.58).
        static readonly GridInfo Grid = new GridInfo
        {
            Width = 310, Height = 210, Resolution = 0.05f, Origin = new Vector2(-1.52f, -3.58f),
        };

        static void AssertV2(Vector2 actual, Vector2 expected, float tol = 1e-4f) =>
            Assert.That(Vector2.Distance(actual, expected), Is.LessThan(tol), $"{actual} != {expected}");

        [Test]
        public void MapWorldRoundTrip()
        {
            var m = new Vector2(2.5f, -1.25f);
            var w = MapGridMath.MapToWorld(m, 0.3f);
            Assert.That(Vector3.Distance(w, new Vector3(1.25f, 0.3f, 2.5f)), Is.LessThan(1e-5f));
            AssertV2(MapGridMath.WorldToMap(w), m);
        }

        [Test]
        public void PanelLocalRoundTrip()
        {
            var center = new Vector3(1f, 0f, -2f);
            var world = new Vector3(3f, 0.5f, 0f);
            var local = MapGridMath.WorldToPanelLocal(world, center, 0.1f);
            Assert.That(Vector3.Distance(local, new Vector3(0.2f, 0.05f, 0.2f)), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(MapGridMath.PanelLocalToWorld(local, center, 0.1f), world), Is.LessThan(1e-4f));
        }

        [Test]
        public void CellIndexingRowZeroIsLowestY()
        {
            Assert.IsTrue(MapGridMath.TryGetCell(Grid, new Vector2(-1.51f, -3.57f), out int c, out int r));
            Assert.That((c, r), Is.EqualTo((0, 0)));
            Assert.IsTrue(MapGridMath.TryGetCell(Grid, new Vector2(0f, 0f), out c, out r));
            Assert.That(c, Is.EqualTo(30));  // (0 + 1.52) / 0.05 = 30.4
            Assert.That(r, Is.EqualTo(71));  // (0 + 3.58) / 0.05 = 71.6
            Assert.That(MapGridMath.CellIndex(Grid, c, r), Is.EqualTo(71 * 310 + 30));
            AssertV2(MapGridMath.CellCenter(Grid, 0, 0), new Vector2(-1.495f, -3.555f));
            AssertV2(MapGridMath.CellCenter(Grid, 30, 71), new Vector2(0.005f, -0.005f));
        }

        [Test]
        public void ContainsEdges()
        {
            Assert.IsTrue(MapGridMath.Contains(Grid, Grid.Origin));
            Assert.IsFalse(MapGridMath.Contains(Grid, Grid.Origin + Grid.Size));
            Assert.IsTrue(MapGridMath.Contains(Grid, Grid.Origin + Grid.Size - new Vector2(0.01f, 0.01f)));
            Assert.IsFalse(MapGridMath.Contains(Grid, Grid.Origin - new Vector2(0.01f, 0f)));
            Assert.IsFalse(MapGridMath.Contains(default(GridInfo), Vector2.zero));
            Assert.IsFalse(MapGridMath.TryGetCell(Grid, new Vector2(100f, 0f), out _, out _));
        }

        [Test]
        public void CenterAndWorldSizeAxes()
        {
            AssertV2(MapGridMath.Center(Grid), new Vector2(-1.52f + 7.75f, -3.58f + 5.25f));
            var size = MapGridMath.WorldSizeXZ(Grid);
            Assert.That(size.x, Is.EqualTo(10.5f).Within(1e-4f));  // Unity x extent = Height * res
            Assert.That(size.y, Is.EqualTo(15.5f).Within(1e-4f));  // Unity z extent = Width * res
            var wc = MapGridMath.WorldCenter(Grid);
            Assert.That(wc.z, Is.EqualTo(6.23f).Within(1e-4f));
            Assert.That(wc.x, Is.EqualTo(-1.67f).Within(1e-4f));
        }

        [Test]
        public void OccupancyToColorsClassifies()
        {
            Color32 u = new Color32(1, 1, 1, 255), f = new Color32(2, 2, 2, 255), o = new Color32(3, 3, 3, 255);
            var data = new sbyte[] { -1, 0, 64, 65, 100 };
            var outp = new Color32[5];
            MapGridMath.OccupancyToColors(data, outp, u, f, o);
            Assert.That(outp, Is.EqualTo(new[] { u, f, f, o, o }));
            Assert.Throws<ArgumentException>(() => MapGridMath.OccupancyToColors(data, new Color32[4], u, f, o));
        }

        [Test]
        public void MaskToColorsThreshold()
        {
            Color32 on = new Color32(9, 9, 9, 255), off = new Color32(0, 0, 0, 0);
            var outp = new Color32[3];
            MapGridMath.MaskToColors(new sbyte[] { 0, 49, 100 }, outp, on, off);
            Assert.That(outp, Is.EqualTo(new[] { off, off, on }));
        }

        [Test]
        public void IsFreeCellRequiresKnownAndFree()
        {
            var g = new GridInfo { Width = 2, Height = 2, Resolution = 1f, Origin = Vector2.zero };
            var data = new sbyte[] { 0, 100, -1, 30 };  // row 0: (0,0)=free (1,0)=occ; row 1: (0,1)=unknown (1,1)=free
            Assert.IsTrue(MapGridMath.IsFreeCell(data, g, new Vector2(0.5f, 0.5f)));
            Assert.IsFalse(MapGridMath.IsFreeCell(data, g, new Vector2(1.5f, 0.5f)));
            Assert.IsFalse(MapGridMath.IsFreeCell(data, g, new Vector2(0.5f, 1.5f)));
            Assert.IsTrue(MapGridMath.IsFreeCell(data, g, new Vector2(1.5f, 1.5f)));
            Assert.IsFalse(MapGridMath.IsFreeCell(data, g, new Vector2(5f, 5f)));
        }

        [Test]
        public void ResampleKeepsSpacingAndEndpoints()
        {
            var pts = new List<Vector2>();
            for (int i = 0; i <= 100; i++) pts.Add(new Vector2(i * 0.01f, 0f));  // 1 m long, 1 cm steps
            var r = MapGridMath.Resample(pts, 0.1f, 1000);
            AssertV2(r[0], pts[0]);
            AssertV2(r[r.Count - 1], pts[100]);
            for (int i = 1; i < r.Count - 1; i++)
                Assert.That(Vector2.Distance(r[i - 1], r[i]), Is.GreaterThanOrEqualTo(0.1f - 1e-4f));
            Assert.That(r.Count, Is.InRange(10, 12));
        }

        [Test]
        public void ResampleDecimatesToMaxPointsKeepingEndpoints()
        {
            var pts = new List<Vector2>();
            for (int i = 0; i <= 100; i++) pts.Add(new Vector2(i, 0f));
            var r = MapGridMath.Resample(pts, 0.5f, 10);
            Assert.That(r.Count, Is.EqualTo(10));
            AssertV2(r[0], pts[0]);
            AssertV2(r[9], pts[100]);
        }

        [Test]
        public void ResampleHandlesEmptyAndSinglePoint()
        {
            Assert.That(MapGridMath.Resample(new List<Vector2>(), 0.1f, 10).Count, Is.EqualTo(0));
            Assert.That(MapGridMath.Resample(new[] { Vector2.one }, 0.1f, 10).Count, Is.EqualTo(1));
        }

        [Test]
        public void PathLengthAndAppendIfFar()
        {
            Assert.That(MapGridMath.PathLength(new[] { Vector2.zero, new Vector2(3f, 4f), new Vector2(3f, 5f) }), Is.EqualTo(6f).Within(1e-5f));
            Assert.That(MapGridMath.PathLength(new List<Vector2>()), Is.EqualTo(0f));
            var live = new List<Vector2>();
            Assert.IsTrue(MapGridMath.AppendIfFar(live, Vector2.zero, 0.1f));
            Assert.IsFalse(MapGridMath.AppendIfFar(live, new Vector2(0.05f, 0f), 0.1f));
            Assert.IsTrue(MapGridMath.AppendIfFar(live, new Vector2(0.2f, 0f), 0.1f));
            Assert.That(live.Count, Is.EqualTo(2));
        }
    }
}
