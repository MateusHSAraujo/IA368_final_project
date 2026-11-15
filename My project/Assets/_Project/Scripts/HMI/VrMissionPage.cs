using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VRRobot.HMI
{
    /// <summary>
    /// Mission page of the VR menu (the Spatial Menu of the project plan): opens/closes the Holographic Tactical Table,
    /// selects the pointing tool and the no-go brush, controls the active mission (pause / resume / cancel) and shows
    /// the ROS, bridge and mission status. Tolerates a missing <see cref="RosHmiClient"/>.
    /// </summary>
    public class VrMissionPage : MonoBehaviour
    {
        [Header("Scene references (fall back to FindFirstObjectByType)")]
        [SerializeField] HolographicTacticalTable _table;
        [SerializeField] TacticalToolController _tools;

        [Header("Table")]
        [SerializeField] Button _tableButton;
        [SerializeField] TMP_Text _tableLabel;

        [Header("Tools (None, Goal, Path, No-go)")]
        [SerializeField] Button[] _toolButtons;

        [Header("Brush (S, M, L)")]
        [SerializeField, Tooltip("Row that is visible only with the No-go tool.")] GameObject _brushRow;
        [SerializeField] Button[] _brushButtons;
        [SerializeField] TMP_Text[] _brushLabels;

        [Header("Mission control")]
        [SerializeField] Button _pauseButton;
        [SerializeField] Button _resumeButton;
        [SerializeField] Button _cancelButton;

        [Header("Status")]
        [SerializeField] TMP_Text _rosLabel;
        [SerializeField] TMP_Text _missionLabel;
        [SerializeField] TMP_Text _messageLabel;

        static readonly Color Ok = new Color(0.75f, 1f, 0.75f);
        static readonly Color Bad = new Color(1f, 0.45f, 0.4f);

        RosHmiClient _ros;

        void Awake()
        {
            if (_table == null) _table = FindFirstObjectByType<HolographicTacticalTable>(FindObjectsInactive.Include);
            if (_tools == null) _tools = FindFirstObjectByType<TacticalToolController>(FindObjectsInactive.Include);

            if (_tableButton != null) _tableButton.onClick.AddListener(() => { if (_table != null) _table.Toggle(); });
            for (int i = 0; i < _toolButtons.Length; i++)
            {
                var tool = (TacticalTool)i;
                if (_toolButtons[i] != null) _toolButtons[i].onClick.AddListener(() => { if (_tools != null) _tools.SetTool(tool); });
            }
            for (int i = 0; i < _brushButtons.Length; i++)
            {
                int index = i;
                if (_brushButtons[i] != null) _brushButtons[i].onClick.AddListener(() => { if (_tools != null) _tools.SetBrush(index); });
            }
            if (_pauseButton != null) _pauseButton.onClick.AddListener(() => Send("PAUSE"));
            if (_resumeButton != null) _resumeButton.onClick.AddListener(() => Send("RESUME"));
            if (_cancelButton != null) _cancelButton.onClick.AddListener(() => Send("CANCEL"));
        }

        void OnEnable()
        {
            _ros = RosHmiClient.Instance;
            if (_table != null) _table.OpenChanged += OnTableChanged;
            if (_tools != null)
            {
                _tools.ToolChanged += OnToolChanged;
                _tools.BrushChanged += RefreshTools;
            }
            if (_ros != null)
            {
                _ros.ConnectionChanged += OnConnectionChanged;
                _ros.BridgeStatusChanged += OnBridgeChanged;
                _ros.MissionStateChanged += OnMissionChanged;
            }
            RefreshAll();
        }

        void OnDisable()
        {
            if (_table != null) _table.OpenChanged -= OnTableChanged;
            if (_tools != null)
            {
                _tools.ToolChanged -= OnToolChanged;
                _tools.BrushChanged -= RefreshTools;
            }
            if (_ros != null)
            {
                _ros.ConnectionChanged -= OnConnectionChanged;
                _ros.BridgeStatusChanged -= OnBridgeChanged;
                _ros.MissionStateChanged -= OnMissionChanged;
            }
            _ros = null;
        }

        void Send(string cmd)
        {
            if (_ros != null) _ros.SendMissionCmd(cmd);
        }

        void OnTableChanged(bool open) => RefreshTable();
        void OnToolChanged(TacticalTool tool) => RefreshTools();
        void OnConnectionChanged(bool connected) => RefreshStatus();
        void OnBridgeChanged(string status) => RefreshStatus();
        void OnMissionChanged(MissionState state) => RefreshStatus();

        void RefreshAll()
        {
            RefreshTable();
            RefreshTools();
            RefreshStatus();
        }

        void RefreshTable()
        {
            bool open = _table != null && _table.IsOpen;
            if (_tableLabel != null) _tableLabel.text = open ? "Close table" : "Open table";
            if (_tableButton != null) _tableButton.interactable = _table != null;
        }

        void RefreshTools()
        {
            var current = _tools != null ? _tools.Tool : TacticalTool.None;
            for (int i = 0; i < _toolButtons.Length; i++)
                if (_toolButtons[i] != null) _toolButtons[i].interactable = _tools != null && i != (int)current;

            if (_brushRow != null) _brushRow.SetActive(_tools != null && current == TacticalTool.NoGo);
            if (_tools == null) return;
            for (int i = 0; i < _brushButtons.Length; i++)
            {
                if (_brushButtons[i] == null) continue;
                bool exists = i < _tools.BrushCount;
                _brushButtons[i].gameObject.SetActive(exists);
                _brushButtons[i].interactable = i != _tools.BrushIndex;
                if (exists && i < _brushLabels.Length && _brushLabels[i] != null)
                {
                    float r = _tools.BrushRadiusAt(i);
                    _brushLabels[i].text = r.ToString("0.00", CultureInfo.InvariantCulture) + " m";
                }
            }
        }

        /// <summary>Refreshes the ROS, mission and mission-control state.</summary>
        void RefreshStatus()
        {
            bool connected = _ros != null && _ros.IsConnected;
            var m = _ros != null ? _ros.Mission : null;

            if (_rosLabel != null)
            {
                if (_ros == null)
                {
                    _rosLabel.text = "ROS: not available";
                    _rosLabel.color = Bad;
                }
                else
                {
                    // The last bridge status is stale while ROS is disconnected.
                    string bridge = !connected || string.IsNullOrEmpty(_ros.BridgeStatus) ? "unknown" : _ros.BridgeStatus;
                    _rosLabel.text = $"ROS: {(connected ? "connected" : "disconnected")} | Bridge: {bridge}";
                    _rosLabel.color = connected && bridge == "connected" ? Ok : Bad;
                }
            }

            if (_missionLabel != null)
            {
                if (m == null)
                    _missionLabel.text = "Mission: -";
                else
                {
                    string extra = m.mode != null ? $" ({m.mode}, {m.remaining.ToString(CultureInfo.InvariantCulture)} left)" : "";
                    _missionLabel.text = $"Mission: {m.state}{extra}";
                }
            }
            if (_messageLabel != null) _messageLabel.text = m != null && !string.IsNullOrEmpty(m.message) ? m.message : "";

            if (_pauseButton != null) _pauseButton.interactable = connected && m != null && m.IsNavigating;
            if (_resumeButton != null) _resumeButton.interactable = connected && m != null && m.IsPaused;
            if (_cancelButton != null) _cancelButton.interactable = connected && m != null && m.IsActive;
        }
    }
}
