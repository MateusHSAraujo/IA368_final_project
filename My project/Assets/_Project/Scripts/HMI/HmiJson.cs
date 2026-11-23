using System;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace VRRobot.HMI
{
    /// <summary>Payload of <c>/hmi/mission_state</c>.</summary>
    [Serializable]
    public class MissionState
    {
        public string state;
        public string mode;
        public int remaining;
        public string message;

        /// <summary>True while a nonzero /cmd_vel_teleop arrived within 0.5 s (supervisor field; absent in old payloads, so false).</summary>
        public bool teleop;

        public bool IsNavigating => state == "NAVIGATING";
        public bool IsPaused => state == "PAUSED";
        public bool IsActive => IsNavigating || IsPaused;
    }

    /// <summary>Payload of <c>/hmi/map_state</c>.</summary>
    [Serializable]
    public class MapState
    {
        public string state;
        public string map;
        public string[] maps;
        public bool ok;
        public string message;

        public bool IsBusy => state == "BUSY";
    }

    /// <summary>Parsing and validation helpers for the JSON strings exchanged with the mission supervisor.</summary>
    public static class HmiJson
    {
        static readonly Regex MapNameRegex = new Regex("^[A-Za-z0-9_-]{1,40}\\z", RegexOptions.Compiled);

        public static bool TryParseMission(string json, out MissionState s)
        {
            s = null;
            try
            {
                s = JsonUtility.FromJson<MissionState>(json);
                if (s == null) return false;
                if (string.IsNullOrEmpty(s.mode)) s.mode = null;
                return true;
            }
            catch (Exception)
            {
                s = null;
                return false;
            }
        }

        public static bool TryParseMap(string json, out MapState s)
        {
            s = null;
            try
            {
                s = JsonUtility.FromJson<MapState>(json);
                if (s == null) return false;
                if (string.IsNullOrEmpty(s.map)) s.map = null;
                if (s.maps == null) s.maps = new string[0];
                return true;
            }
            catch (Exception)
            {
                s = null;
                return false;
            }
        }

        public static bool IsValidMapName(string name) => name != null && MapNameRegex.IsMatch(name);

        public static string TimestampMapName(DateTime t) =>
            "map_" + t.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
    }
}
