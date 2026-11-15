using System;
using System.Collections.Generic;
using System.Globalization;
using RosMessageTypes.Geometry;
using RosMessageTypes.Nav;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using VRRobot.Core;

namespace VRRobot.HMI
{
    /// <summary>
    /// Single owner of the ROS I/O of the HMI (talks only to the mission supervisor). Caches the latest values and
    /// raises events on the main thread. The <c>map</c> frame equals the Unity world (see <see cref="CoordinateConverter"/>).
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class RosHmiClient : MonoBehaviour
    {
        [SerializeField, Tooltip("Optional; otherwise ROSConnection.GetOrCreateInstance() is used.")] ROSConnection _ros;
        [SerializeField] bool _hideHud = true;

        [Header("Published topics")]
        [SerializeField] string _goalTopic = "/hmi/goal";
        [SerializeField] string _pathTopic = "/hmi/path";
        [SerializeField] string _missionCmdTopic = "/hmi/mission_cmd";
        [SerializeField] string _keepoutStrokeTopic = "/hmi/keepout_stroke";
        [SerializeField] string _mapCmdTopic = "/hmi/map_cmd";

        [Header("Subscribed topics")]
        [SerializeField, Tooltip("Volatile copy of /map republished by the supervisor (the endpoint cannot receive the " +
                                 "transient_local /map, which slam_toolbox only publishes when the map changes).")]
        string _mapTopic = "/hmi/map";
        [SerializeField] string _keepoutMaskTopic = "/hmi/keepout_mask";
        [SerializeField] string _robotPoseTopic = "/hmi/robot_pose";
        [SerializeField] string _planTopic = "/plan";
        [SerializeField] string _missionStateTopic = "/hmi/mission_state";
        [SerializeField] string _mapStateTopic = "/hmi/map_state";
        [SerializeField] string _bridgeStatusTopic = "/myRobot/bridge_status";

        const string MapFrame = "map";
        const string LogPrefix = "[HMI] ";

        public static RosHmiClient Instance { get; private set; }

        public bool IsConnected { get; private set; }
        public event Action<bool> ConnectionChanged;

        public OccupancyGridMsg LastMap { get; private set; }
        public GridInfo LastMapInfo { get; private set; }
        public event Action<OccupancyGridMsg, GridInfo> MapReceived;

        public OccupancyGridMsg LastKeepoutMask { get; private set; }
        public GridInfo LastKeepoutInfo { get; private set; }
        public event Action<OccupancyGridMsg, GridInfo> KeepoutMaskReceived;

        public bool HasRobotPose { get; private set; }
        public Vector3 RobotPosition { get; private set; }
        public Quaternion RobotRotation { get; private set; } = Quaternion.identity;
        public float LastRobotPoseTime { get; private set; }
        public event Action<Vector3, Quaternion> RobotPoseReceived;

        public IReadOnlyList<Vector3> LastPlan { get; private set; } = Array.Empty<Vector3>();
        public float LastPlanTime { get; private set; }
        public event Action<IReadOnlyList<Vector3>> PlanReceived;

        public MissionState Mission { get; private set; }
        public event Action<MissionState> MissionStateChanged;

        public MapState MapState { get; private set; }
        public event Action<MapState> MapStateChanged;

        public string BridgeStatus { get; private set; }
        public event Action<string> BridgeStatusChanged;

        ROSConnection _conn;
        bool _started;
        bool _warnedYaw;

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_conn == null) return;
            foreach (var t in new[] { _mapTopic, _keepoutMaskTopic, _robotPoseTopic, _planTopic, _missionStateTopic, _mapStateTopic, _bridgeStatusTopic })
                _conn.Unsubscribe(t);
        }

        void Start()
        {
            _conn = _ros != null ? _ros : ROSConnection.GetOrCreateInstance();
            if (_hideHud) _conn.ShowHud = false;

            _conn.RegisterPublisher<PoseStampedMsg>(_goalTopic);
            _conn.RegisterPublisher<PathMsg>(_pathTopic);
            _conn.RegisterPublisher<StringMsg>(_missionCmdTopic);
            _conn.RegisterPublisher<PolygonStampedMsg>(_keepoutStrokeTopic);
            _conn.RegisterPublisher<StringMsg>(_mapCmdTopic);

            _conn.Subscribe<OccupancyGridMsg>(_mapTopic, OnMap);
            _conn.Subscribe<OccupancyGridMsg>(_keepoutMaskTopic, OnKeepoutMask);
            _conn.Subscribe<PoseStampedMsg>(_robotPoseTopic, OnRobotPose);
            _conn.Subscribe<PathMsg>(_planTopic, OnPlan);
            _conn.Subscribe<StringMsg>(_missionStateTopic, OnMissionState);
            _conn.Subscribe<StringMsg>(_mapStateTopic, OnMapState);
            _conn.Subscribe<StringMsg>(_bridgeStatusTopic, OnBridgeStatus);
            _started = true;
        }

        void Update()
        {
            if (!_started) return;
            bool connected = _conn.HasConnectionThread && !_conn.HasConnectionError;
            if (connected == IsConnected) return;
            IsConnected = connected;
            Debug.Log(LogPrefix + (connected ? "connected to ROS" : "disconnected from ROS"));
            ConnectionChanged?.Invoke(connected);
        }

        // ---- Subscriptions -------------------------------------------------------------------------------------

        void OnMap(OccupancyGridMsg msg)
        {
            var info = ToGridInfo(msg.info);
            bool changed = !SameGeometry(LastMapInfo, info);
            LastMap = msg;
            LastMapInfo = info;
            if (changed) Debug.Log(LogPrefix + "/map " + Describe(info));
            MapReceived?.Invoke(msg, info);
        }

        void OnKeepoutMask(OccupancyGridMsg msg)
        {
            var info = ToGridInfo(msg.info);
            bool changed = !SameGeometry(LastKeepoutInfo, info);
            LastKeepoutMask = msg;
            LastKeepoutInfo = info;
            if (changed) Debug.Log(LogPrefix + "/hmi/keepout_mask " + Describe(info));
            KeepoutMaskReceived?.Invoke(msg, info);
        }

        void OnRobotPose(PoseStampedMsg msg)
        {
            string frame = msg.header.frame_id;
            if (frame != MapFrame && frame != "odom") return;
            var p = msg.pose.position;
            var q = msg.pose.orientation;
            RobotPosition = CoordinateConverter.PositionToUnity((float)p.x, (float)p.y, (float)p.z);
            var rot = CoordinateConverter.RotationToUnity((float)q.x, (float)q.y, (float)q.z, (float)q.w);
            float n = Mathf.Sqrt(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w);
            RobotRotation = n < 1e-8f ? Quaternion.identity : new Quaternion(rot.x / n, rot.y / n, rot.z / n, rot.w / n);
            HasRobotPose = true;
            LastRobotPoseTime = Time.time;
            RobotPoseReceived?.Invoke(RobotPosition, RobotRotation);
        }

        void OnPlan(PathMsg msg)
        {
            var points = new List<Vector3>(msg.poses.Length);
            foreach (var ps in msg.poses)
                points.Add(CoordinateConverter.PositionToUnity((float)ps.pose.position.x, (float)ps.pose.position.y, 0f));
            LastPlan = points;
            LastPlanTime = Time.time;
            PlanReceived?.Invoke(points);
        }

        void OnMissionState(StringMsg msg)
        {
            if (!HmiJson.TryParseMission(msg.data, out var s))
            {
                Debug.LogWarning(LogPrefix + "invalid mission_state JSON: " + msg.data);
                return;
            }
            var old = Mission;
            Mission = s;
            if (old != null && old.state != s.state && (!s.IsActive || (!old.IsActive && s.state == "NAVIGATING")))
            {
                // The mission ended or a new one started: the cached route is stale.
                LastPlan = Array.Empty<Vector3>();
                LastPlanTime = Time.time;
                PlanReceived?.Invoke(LastPlan);
            }
            if (old == null || old.state != s.state || old.message != s.message)
                Debug.Log(LogPrefix + $"mission {s.state} mode={s.mode ?? "-"} remaining={s.remaining} \"{s.message}\"");
            MissionStateChanged?.Invoke(s);
        }

        void OnMapState(StringMsg msg)
        {
            if (!HmiJson.TryParseMap(msg.data, out var s))
            {
                Debug.LogWarning(LogPrefix + "invalid map_state JSON: " + msg.data);
                return;
            }
            var old = MapState;
            MapState = s;
            if (old == null || old.state != s.state || old.map != s.map || old.ok != s.ok || old.message != s.message
                || !string.Join(",", old.maps).Equals(string.Join(",", s.maps)))
                Debug.Log(LogPrefix + $"map state {s.state} map={s.map ?? "-"} maps=[{string.Join(",", s.maps)}] ok={s.ok} \"{s.message}\"");
            MapStateChanged?.Invoke(s);
        }

        void OnBridgeStatus(StringMsg msg)
        {
            if (msg.data == BridgeStatus) return;
            BridgeStatus = msg.data;
            Debug.Log(LogPrefix + "bridge status: " + BridgeStatus);
            BridgeStatusChanged?.Invoke(BridgeStatus);
        }

        /// <summary>Converts the grid metadata; warns once when the origin yaw is not ~0 (it is ignored).</summary>
        public static GridInfo ToGridInfo(MapMetaDataMsg info)
        {
            var q = info.origin.orientation;
            float yaw = 2f * Mathf.Atan2((float)q.z, (float)q.w);
            if (Mathf.Abs(Mathf.DeltaAngle(0f, yaw * Mathf.Rad2Deg)) > 1f && Instance != null && !Instance._warnedYaw)
            {
                Instance._warnedYaw = true;
                Debug.LogWarning(LogPrefix + "map origin yaw is not zero; it is ignored");
            }
            return new GridInfo
            {
                Width = (int)info.width,
                Height = (int)info.height,
                Resolution = info.resolution,
                Origin = new Vector2((float)info.origin.position.x, (float)info.origin.position.y),
            };
        }

        // ---- Senders -------------------------------------------------------------------------------------------

        public void SendGoal(Vector2 mapXY)
        {
            if (!CanSend("goal")) return;
            var msg = new PoseStampedMsg { header = new HeaderMsg { frame_id = MapFrame }, pose = ZeroOrientationPose(mapXY) };
            _conn.Publish(_goalTopic, msg);
            Debug.Log(LogPrefix + "goal " + Fmt(mapXY));
        }

        public void SendPath(IList<Vector2> mapXY)
        {
            if (!CanSend("path")) return;
            var poses = new PoseStampedMsg[mapXY.Count];
            for (int i = 0; i < poses.Length; i++)
                poses[i] = new PoseStampedMsg { header = new HeaderMsg { frame_id = MapFrame }, pose = ZeroOrientationPose(mapXY[i]) };
            var msg = new PathMsg { header = new HeaderMsg { frame_id = MapFrame }, poses = poses };
            _conn.Publish(_pathTopic, msg);
            Debug.Log(LogPrefix + $"path with {poses.Length} poses, {MapGridMath.PathLength(mapXY).ToString("F2", CultureInfo.InvariantCulture)} m");
        }

        public void SendMissionCmd(string cmd)
        {
            if (!CanSend("mission_cmd")) return;
            _conn.Publish(_missionCmdTopic, new StringMsg(cmd));
            Debug.Log(LogPrefix + "mission_cmd " + cmd);
        }

        public void SendStroke(IList<Vector2> mapXY, float radius)
        {
            if (!CanSend("keepout stroke")) return;
            var pts = new Point32Msg[mapXY.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = new Point32Msg(mapXY[i].x, mapXY[i].y, radius);
            _conn.Publish(_keepoutStrokeTopic, StrokeMsg(pts));
            Debug.Log(LogPrefix + $"keepout stroke with {pts.Length} points, radius {radius.ToString("F2", CultureInfo.InvariantCulture)} m");
        }

        public void ClearKeepout()
        {
            if (!CanSend("keepout clear")) return;
            _conn.Publish(_keepoutStrokeTopic, StrokeMsg(new Point32Msg[0]));
            Debug.Log(LogPrefix + "keepout clear");
        }

        public void SendMapCmd(string cmd)
        {
            if (!CanSend("map_cmd")) return;
            _conn.Publish(_mapCmdTopic, new StringMsg(cmd));
            Debug.Log(LogPrefix + "map_cmd " + cmd);
        }

        // ---- Helpers -------------------------------------------------------------------------------------------

        bool CanSend(string what)
        {
            if (_started) return true;
            Debug.LogWarning(LogPrefix + "not started, dropping " + what);
            return false;
        }

        static PolygonStampedMsg StrokeMsg(Point32Msg[] points) =>
            new PolygonStampedMsg { header = new HeaderMsg { frame_id = MapFrame }, polygon = new PolygonMsg(points) };

        /// <summary>Pose with an all-zero quaternion: the supervisor then faces the direction of travel.</summary>
        static PoseMsg ZeroOrientationPose(Vector2 mapXY) => new PoseMsg
        {
            position = new PointMsg(mapXY.x, mapXY.y, 0.0),
            orientation = new QuaternionMsg(0.0, 0.0, 0.0, 0.0),
        };

        static bool SameGeometry(GridInfo a, GridInfo b) =>
            a.Width == b.Width && a.Height == b.Height && Mathf.Approximately(a.Resolution, b.Resolution) && a.Origin == b.Origin;

        static string Describe(GridInfo g) =>
            $"{g.Width}x{g.Height} res {g.Resolution.ToString("F2", CultureInfo.InvariantCulture)} origin {Fmt(g.Origin)}";

        static string Fmt(Vector2 v) =>
            $"({v.x.ToString("F2", CultureInfo.InvariantCulture)}, {v.y.ToString("F2", CultureInfo.InvariantCulture)})";
    }
}
