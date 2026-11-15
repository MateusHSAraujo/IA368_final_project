using System;
using NUnit.Framework;
using VRRobot.HMI;

namespace VRRobot.Tests
{
    public class HmiJsonTests
    {
        [Test]
        public void MissionWithNullMode()
        {
            Assert.IsTrue(HmiJson.TryParseMission("{\"state\": \"IDLE\", \"mode\": null, \"remaining\": 0, \"message\": \"\"}", out var s));
            Assert.That(s.state, Is.EqualTo("IDLE"));
            Assert.IsNull(s.mode);
            Assert.IsFalse(s.IsActive);
        }

        [Test]
        public void MissionNavigatingAndPausedFlags()
        {
            Assert.IsTrue(HmiJson.TryParseMission("{\"state\":\"NAVIGATING\",\"mode\":\"path\",\"remaining\":3,\"message\":\"ok\"}", out var s));
            Assert.That(s.mode, Is.EqualTo("path"));
            Assert.That(s.remaining, Is.EqualTo(3));
            Assert.IsTrue(s.IsNavigating);
            Assert.IsTrue(s.IsActive);
            Assert.IsTrue(HmiJson.TryParseMission("{\"state\":\"PAUSED\"}", out s));
            Assert.IsTrue(s.IsPaused);
            Assert.IsTrue(s.IsActive);
        }

        [Test]
        public void MapStateWithList()
        {
            Assert.IsTrue(HmiJson.TryParseMap("{\"state\":\"READY\",\"map\":\"scene_house\",\"maps\":[\"scene_house\",\"map_1\"],\"ok\":true,\"message\":\"\"}", out var s));
            Assert.That(s.map, Is.EqualTo("scene_house"));
            Assert.That(s.maps, Is.EqualTo(new[] { "scene_house", "map_1" }));
            Assert.IsTrue(s.ok);
            Assert.IsFalse(s.IsBusy);
        }

        [Test]
        public void MapStateNullMapAndMissingList()
        {
            Assert.IsTrue(HmiJson.TryParseMap("{\"state\":\"BUSY\",\"map\":null}", out var s));
            Assert.IsNull(s.map);
            Assert.IsNotNull(s.maps);
            Assert.That(s.maps.Length, Is.EqualTo(0));
            Assert.IsTrue(s.IsBusy);
        }

        [Test]
        public void MalformedJsonFails()
        {
            Assert.IsFalse(HmiJson.TryParseMission("not json", out var m));
            Assert.IsNull(m);
            Assert.IsFalse(HmiJson.TryParseMap("{broken", out var s));
            Assert.IsNull(s);
            Assert.IsFalse(HmiJson.TryParseMission(null, out _));
        }

        [Test]
        public void MapNameValidation()
        {
            Assert.IsTrue(HmiJson.IsValidMapName("scene_house"));
            Assert.IsTrue(HmiJson.IsValidMapName("a-B_9"));
            Assert.IsTrue(HmiJson.IsValidMapName(new string('a', 40)));
            Assert.IsFalse(HmiJson.IsValidMapName(new string('a', 41)));
            Assert.IsFalse(HmiJson.IsValidMapName(""));
            Assert.IsFalse(HmiJson.IsValidMapName(null));
            Assert.IsFalse(HmiJson.IsValidMapName("my map"));
            Assert.IsFalse(HmiJson.IsValidMapName("a/b"));
            Assert.IsFalse(HmiJson.IsValidMapName("name\n"));
        }

        [Test]
        public void TimestampMapNameFormat()
        {
            Assert.That(HmiJson.TimestampMapName(new DateTime(2026, 11, 5, 9, 7, 30)), Is.EqualTo("map_20261105_0907"));
            Assert.IsTrue(HmiJson.IsValidMapName(HmiJson.TimestampMapName(DateTime.Now)));
        }
    }
}
