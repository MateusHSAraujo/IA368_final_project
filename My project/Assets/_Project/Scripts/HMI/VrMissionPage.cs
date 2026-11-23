using System.Globalization;
using TMPro;
using VRRobot.XR;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace VRRobot.HMI
{
    /// <summary>
    /// Mission page of the VR menu (the Spatial Menu of the project plan): opens/closes the operations panel,
    /// controls the active mission (pause / resume / cancel), starts/stops the teleop mode and shows the ROS, bridge and
    /// mission status. The tool
    /// picker on this page is a <see cref="ToolSelector"/>. Tolerates a missing <see cref="RosHmiClient"/>.
    /// </summary>
    public class VrMissionPage : MonoBehaviour
    {
        [Header("Scene references (fall back to FindFirstObjectByType)")]
        [SerializeField, FormerlySerializedAs("_table")] OperationsPanel _panel;
        [SerializeField] FirstPersonTeleopController _teleop;

        [Header("Operations panel")]
        [SerializeField, FormerlySerializedAs("_tableButton")] Button _panelButton;
        [SerializeField, FormerlySerializedAs("_tableLabel")] TMP_Text _panelLabel;

        [Header("Mission control")]
        [SerializeField] Button _pauseButton;
        [SerializeField] Button _resumeButton;
        [SerializeField] Button _cancelButton;

        [Header("Teleop")]
        [SerializeField, Tooltip("Starts/stops the teleop mode (same as X on the left controller).")] Button _teleopButton;
        [SerializeField] TMP_Text _teleopLabel;

        [Header("Status")]
        [SerializeField] TMP_Text _rosLabel;
        [SerializeField] TMP_Text _missionLabel;
        [SerializeField] TMP_Text _messageLabel;
        [SerializeField, Tooltip("Optional chips; when assigned they show the ROS and mission state.")] StatusChip _rosChip;
        [SerializeField] StatusChip _missionChip;

        static readonly Color Ok = new Color(0.75f, 1f, 0.75f);
        static readonly Color Bad = new Color(1f, 0.45f, 0.4f);

        RosHmiClient _ros;

        void Awake()
        {
            if (_panel == null) _panel = FindFirstObjectByType<OperationsPanel>(FindObjectsInactive.Include);
            if (_teleop == null) _teleop = FindFirstObjectByType<FirstPersonTeleopController>(FindObjectsInactive.Include);

            if (_panelButton != null) _panelButton.onClick.AddListener(() => { if (_panel != null) _panel.Toggle(); });
            if (_pauseButton != null) _pauseButton.onClick.AddListener(() => Send("PAUSE"));
            if (_resumeButton != null) _resumeButton.onClick.AddListener(() => Send("RESUME"));
            if (_cancelButton != null) _cancelButton.onClick.AddListener(() => Send("CANCEL"));
            if (_teleopButton != null) _teleopButton.onClick.AddListener(() => { if (_teleop != null) _teleop.Toggle(); });
        }

        void OnEnable()
        {
            _ros = RosHmiClient.Instance;
            if (_panel != null) _panel.OpenChanged += OnPanelChanged;
            if (_teleop != null) _teleop.ActiveChanged += OnTeleopChanged;
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
            if (_panel != null) _panel.OpenChanged -= OnPanelChanged;
            if (_teleop != null) _teleop.ActiveChanged -= OnTeleopChanged;
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

        void OnPanelChanged(bool open) => RefreshPanel();
        void OnTeleopChanged(bool active) => RefreshTeleop();
        void OnConnectionChanged(bool connected) => RefreshStatus();
        void OnBridgeChanged(string status) => RefreshStatus();
        void OnMissionChanged(MissionState state) => RefreshStatus();

        void RefreshAll()
        {
            RefreshPanel();
            RefreshTeleop();
            RefreshStatus();
        }

        void RefreshTeleop()
        {
            bool active = _teleop != null && _teleop.IsActive;
            if (_teleopLabel != null) _teleopLabel.text = active ? "Stop teleop" : "Start teleop";
            if (_teleopButton != null) _teleopButton.interactable = _teleop != null;
        }

        void RefreshPanel()
        {
            bool open = _panel != null && _panel.IsOpen;
            if (_panelLabel != null) _panelLabel.text = open ? "Close panel" : "Open panel";
            if (_panelButton != null) _panelButton.interactable = _panel != null;
        }

        internal static StatusChip.Level MissionLevel(string state)
        {
            switch (state.ToUpperInvariant())
            {
                case "NAVIGATING": return StatusChip.Level.Ok;
                case "PAUSED": return StatusChip.Level.Warn;
                case "FAILED":
                case "CANCELED":
                case "CANCELLED":
                case "ABORTED":
                case "REJECTED":
                case "ERROR": return StatusChip.Level.Error;
                default: return StatusChip.Level.Neutral;
            }
        }

        internal static string TitleCase(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();

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

            if (_rosChip != null)
            {
                if (_ros == null) _rosChip.Set("Unavailable", StatusChip.Level.Error);
                else if (!connected) _rosChip.Set("Disconnected", StatusChip.Level.Error);
                else if (string.IsNullOrEmpty(_ros.BridgeStatus)) _rosChip.Set("Connected", StatusChip.Level.Warn);
                else if (_ros.BridgeStatus == "connected") _rosChip.Set("Connected", StatusChip.Level.Ok);
                else _rosChip.Set("Bridge " + _ros.BridgeStatus, StatusChip.Level.Warn);
            }
            if (_missionChip != null)
            {
                if (m == null || string.IsNullOrEmpty(m.state)) _missionChip.Set("-", StatusChip.Level.Neutral);
                else _missionChip.Set(TitleCase(m.state), MissionLevel(m.state));
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
